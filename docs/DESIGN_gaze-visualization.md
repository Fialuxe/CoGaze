# Gaze Visualization Design Rationale

> **引用ステータス凡例:** ✓ ACM DL / DOI 確認済 ／ ⚠ 内容要確認（論文存在は確認済）／ ? 未確認

---

## 概要

CoGazeでは Expertの視線を Workerに対して3種類の方式で提示する（Circle / Ray / Frustum）。  
各方式の設計根拠、先行研究との差異、および技術的選択の文脈を本文書に記録する。

---

## 1. なぜ視線共有が必要か

リモート協働における視線共有の有効性は複数の研究で確認されている。

- ✓ **Fussell et al., *Human–Computer Interaction* 2004** "Gestures Over Video Streams to Support Remote Collaboration on Physical Tasks" (DOI: 10.1207/s15327051hci1903_3): 遠隔指示において空間的なポインタ（ジェスチャー・アノテーション）はタスク完了時間とコミュニケーションコストを有意に低減した。
- ⚠ **Kirk, Crabtree & Rodden, ECSCW 2005** "Ways of the Hands": 作業者の視野（POV）映像とアノテーション共有を組み合わせることで、指示者による的確な誘導が可能になると報告。*(注: 著者・年の組み合わせ要手動確認)*
- ✓ **McCarley, Leggett & Enright, *Human Factors* 2021** "Shared Gaze Fails to Improve Team Visual Monitoring" (DOI: 10.1177/0018720820902347): 対象への**収束的注意**を要するタスク（Expert→Worker誘導型）では視線共有が有効。分散スキャンを要するタスクでは効果なし。

CoGazeはまさに「Expert が対象物を指示し Worker が手を動かす」収束型タスクであり、視線共有効果が最大化される文脈に該当する（McCarley et al. 2021）。

---

## 2. 平滑化（EMA）の根拠

生の視線データをそのまま描画すると、視覚的ジッターがユーザビリティを著しく損なう。視線共有の効果はカーソル設計に強く依存することが知られており（D'Angelo & Gergle, 2016）、非平滑化カーソルは注意散漫を誘発しうる。

### 実装

`OscGazeInput.cs` の `smoothingAlpha`（Inspector設定可能、**デフォルト α = 0.5**）で指数移動平均（EMA）を適用。

```
smoothed_t = α × raw_t + (1-α) × smoothed_{t-1}
```

| α | 平均遅延 (60Hz) | 特性 |
|---|----------------|------|
| 0.3 | ~39ms | **過剰**。VRで知覚可能なカーソル遅れが生じる |
| **0.5** | **~17ms** | **推奨**。5cmサークルが残留ジッターを視覚的に吸収 |
| 0.7 | ~8ms | ほぼ生データ。ジッターが目立つ場合がある |

- blink 信号（二値）は平滑化対象外
- 初回受信時は生データで初期化（スタートアップスパイクを防止）
- α は Inspector から調整可能。実験前のパイロットで確認すること
- 理想的には **1-euro filter**（fixation中は低α、saccade中は高α）に移行することで遅延と平滑性の両立が可能

> ✓ **D'Angelo & Gergle, CHI 2016** "Gazed and Confused: Understanding and Designing Shared Gaze for Remote Collaboration" (ACM DL: 10.1145/2858036.2858499): 視線共有の表現設計がコラボレーション品質に大きく影響すること、および involuntary fixations が誤った意図として読まれるリスクを指摘。EMA による平滑化はジッター低減と同時に瞬間的な眼球運動（サッカード）のノイズを抑制する。

---

## 3. 各可視化方式と先行研究との比較

### 3.1 Circle（サークル）モード

**先行研究（2Dスクリーンスペース）との差異:**

| 特徴 | 先行研究 | CoGaze Circle |
|------|---------|--------------|
| 投影方式 | 画面へのオーバーレイ（2D） | 3Dレイキャストで実メッシュに投影 |
| サイズ単位 | 任意のピクセル数 | ワールド空間 5cm（視距離不変） |
| 法線整合 | なし（常に画面平行） | 表面法線に垂直（`Quaternion.LookRotation(normal)`） |
| Zファイティング対策 | 未記載 | 法線方向 1mm オフセット |

**論拠:**
- Fussell et al. (2004)、Kirk et al. (2005) はいずれも2Dスクリーンオーバーレイを使用。3Dオブジェクト表面への法線整合投影は本実装が先行研究を超える技術的進歩点。
- ワールド空間でのサイズ固定（5cm）により、作業台との距離によらず一定の視覚的重みを保証。

**残課題:** オレンジ色（1, 0.4, 0）の選択に明示的な文献的根拠なし。高コントラスト確保のための実用的判断。

---

### 3.2 Ray（レイ）モード

**先行研究（2D矢印・ラインオーバーレイ）との差異:**

| 特徴 | 先行研究 | CoGaze Ray |
|------|---------|-----------|
| 形状 | 2D矢印・破線 | 3Dシリンダー（半径 8mm） |
| 視点依存性 | あり（真横から見えなくなる） | なし（全方位から視認可能） |
| 端点表現 | なし（方向のみ） | 白球マーカー（半径 30mm）で着点明示 |
| 空間的体現 | 画面上の記号 | ワールド空間に実体化した視線方向 |

**論拠:**
- 2D矢印は Worker が特定角度から見ると消失・誤読する。3Dシリンダーは「Expertの視線が物理的にここを通っている」という直観的な空間理解を提供。
- 端点（白球）により「Expertが見ているのはここ」という参照先を明確化。Fussell et al. (2004) が報告した空間的ポインタの有効性を三次元で実現。

---

### 3.3 Frustum（フラスタム）モード

**先行研究（単純なFOV表示）との差異:**

| 特徴 | 先行研究 | CoGaze Frustum |
|------|---------|---------------|
| 形状 | 円弧・扇形のFOV指示 | 8頂点の透視投影錐台（近・遠平面付き） |
| FOV適応 | 固定 | タスク依存（Identification: 60° / Assembly: ストリーミングカメラ準拠） |
| 役割別表示 | なし | Expert: 遠平面のみ / Worker: 遠平面＋4側面（非対称表示） |

**論拠:**
- ✓ **Ou, Fussell, Chen, Setlock & Yang, ICMI 2003** (ACM DL: 10.1145/958432.958477) "Gestural Communication over Video Stream: Supporting Multimodal Interaction for Remote Collaborative Physical Tasks": FOV共有に単純な表示を使用。CoGazeの錐台は「Expertが見ている空間の体積」を可視化し、Worker は Expertの視野の3D的広がりを直観的に把握できる。
- **非対称表示** は新規設計: Expert に遠平面を返すことで Expert 自身のレンダリングを妨げず、Worker には側面まで提示して視野の方向と奥行きを伝える。
- FOVのタスク別切り替え（識別タスク: Expert PCカメラ推定 60° vertical / 組立タスク: PCAカメラ推定 90° vertical）は実際の観察映像の視野を反映する設計。

**近年の関連研究（2019–2024）:**

- ✓ **Piumsomboon et al., Frontiers in Robotics and AI 2019** [PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC7805624/): AR/VR非対称ペア16組で FoV錐台・頭部視線レイ・眼球視線レイを比較。**錐台＋視線レイの組み合わせが最善**。錐台単独は相互視線頻度が最低。錐台の透明度が高すぎると対象オブジェクトが隠れるとの報告あり。
- ✓ **Bovo et al., CSCW 2022** [ACM DL](https://dl.acm.org/doi/10.1145/3555615): Cone of Vision（平均注視マップから生成）により視覚的注意の結合が改善。
- ✓ **Punpongsanon et al. (ObserVAR), ISMAR 2019**: 深度適応型錐台（シーンオブジェクトまで延伸）を採用。
- ✓ **GazeMolVR, MUM 2024** [ACM DL](https://dl.acm.org/doi/10.1145/3701571.3701599): GazePoint/GazeArrow/GazeSpotlight/GazeTrail の比較研究。3D協働では単純ドット（GazePoint）が最低評価。

**方向計算の設計（ノイズ緩衝）:**

Frustumの方向には直近 90 サンプル（≈1.5秒 @ 60Hz）の視線座標の**移動平均重心**を使用する。ウェブカメラ由来のランダムノイズは平均で打ち消しあい、実際の注視傾向だけが残る。Bovo et al. (CSCW 2022) の "Cone of Vision"（平均注視マップからビューコーンを生成）と同じアプローチ。

- **Circle / Ray**: 瞬間の視線点（EMAで微調整）→「今まさにどこを見ているか」
- **Frustum**: 移動平均重心 → 「最近どの領域に注意を向けていたか」

これにより各モードが独立した情報粒度を持ち、実験条件間の比較に意味が生まれる。

**CoGaze Frustum の位置づけ:**
- 固定 1.3m は近接作業空間（1〜2m）において文献上有意な問題なし。Piumsomboon et al. はパイロットで実視野角（110°）の半分にスケールしており、本実装の60°もアーム参照に整合。
- **最大の留意点**: 錐台単独は文献で最も弱い設計。CoGazeでは Circle/Ray/Frustum/NoGaze を条件変数として独立させており、「組み合わせでなく各キューの独立効果を測定する」という実験設計上の意図として説明可能。論文にこの点を明記すること。

**設計上の制限と注意:**
- フラスタム長は固定 1.3m。実際の着目点（レイキャスト距離）に追従しない点は改善余地あり。
- `EXPERT_CAMERA_FOV = 60f` は Unityカメラのデフォルト値であり、Expert の実際のモニター視野角（物理サイズ・視距離依存）の実測値ではない。実験実施前に Expert の着席環境で実測・補正することを推奨。
- `streamingFov = 90f` は Quest 3 左カメラの推定値。公式スペックまたは実測で確認すること。

---

## 4. 設計上の留意点（研究文脈）

### 連続視線共有のリスク

> ✓ **D'Angelo & Gergle, CHI 2016**: 視線共有の表現設計がコラボレーション効率と認知負荷に影響する。音声通話との競合（partner の視線を見ることで発話を聞き逃す）リスクを指摘。

CoGazeでは視線可視化を条件変数（Circle / Ray / Frustum / NoGaze）として扱い、NoGaze条件をベースラインとして設定することで、この問題を実験計画上で制御している。

### 空間的グラウンディングの重要性

> ✓ **Wang et al., *Interacting with Computers* 2020, 32(2), 153–169** "Using a Head Pointer or Eye Gaze: The Effect of Gaze on Spatial AR Remote Collaboration for Physical Tasks": 視線精度よりも**空間的グラウンディング**が協働効果の主要因。頭部向きによる近似でも等価な協働支援効果が得られた。

Frustum モードはこの知見と整合する：精密な視線点よりも Expertの「注意空間」全体を伝えることで、Workerの空間理解を支援する。

---

## 5. 参考文献

- ✓ D'Angelo, S., & Gergle, D. (2016). Gazed and confused: Understanding and designing shared gaze for remote collaboration. *Proceedings of CHI 2016*. ACM. https://doi.org/10.1145/2858036.2858499
- ✓ Fussell, S. R., Setlock, L. D., Yang, J., Ou, J., Mauer, E., & Kramer, A. D. I. (2004). Gestures over video streams to support remote collaboration on physical tasks. *Human–Computer Interaction, 19*(3), 273–309. https://doi.org/10.1207/s15327051hci1903_3
- ⚠ Kirk, D., Crabtree, A., & Rodden, T. (2005). Ways of the hands. *ECSCW 2005*. *(著者・年の組み合わせ要手動確認)*
- ✓ McCarley, J. S., Leggett, C. W., & Enright, H. (2021). Shared gaze fails to improve team visual monitoring performance. *Human Factors, 63*(4), 696–705. https://doi.org/10.1177/0018720820902347
- ✓ Ou, J., Fussell, S. R., Chen, X., Setlock, L. D., & Yang, J. (2003). Gestural communication over video stream: Supporting multimodal interaction for remote collaborative physical tasks. *Proceedings of ICMI 2003*. ACM. https://doi.org/10.1145/958432.958477
- ✓ Wang, P., Bai, X., Billinghurst, M., et al. (2020). Using a head pointer or eye gaze: The effect of gaze on spatial AR remote collaboration for physical tasks. *Interacting with Computers, 32*(2), 153–169.
- ✓ Piumsomboon, T., et al. (2019). The effects of sharing awareness cues in collaborative mixed reality. *Frontiers in Robotics and AI*. https://pmc.ncbi.nlm.nih.gov/articles/PMC7805624/
- ✓ Bovo, R., et al. (2022). Cone of Vision as a behavioural cue for VR collaboration. *Proc. ACM Hum.-Comput. Interact. (CSCW 2022)*. https://dl.acm.org/doi/10.1145/3555615
- ✓ Punpongsanon, P., et al. (2019). ObserVAR: Visualization system for observing VR users using AR. *IEEE ISMAR 2019*. https://3dvar.com/Thanyadit2019ObserVAR.pdf
- ✓ GazeMolVR (2024). Sharing eye-gaze cues in a collaborative VR environment for molecular visualization. *MUM 2024*. https://dl.acm.org/doi/10.1145/3701571.3701599
