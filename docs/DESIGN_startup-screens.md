# 設計メモ（合意用ドラフト）: 起動画面 — Desktop(Expert) + HMD(Worker) ＋ 自己チェック ＋ ④復元

> ステータス: **提案・未承認・未実装**。UI / XRインタラクションを詰めるためのドキュメント。コード着手前に sign-off。
> 対象: Runbook 危険ケース「instructions欠落で永久Enter無反応」「同一participant再走でCSV汚染」「participant番号誤入力で条件順崩れ」「Expert再起動で全消失(④)」＋「起動状況が不可視＝フリーズと区別不能」。
> 方針: 自己チェック結果・警告を **Editorコンソールでなく起動画面**に出す。Desktop/HMD 両方。検証は EXE(Expert)+HMD(Worker) 実走。

---

## 0. 現状の起動フロー（コード確認済）
`SceneBootstrapper2.StartupFlow()`(:88):
- `config = StartupConfig.LoadOrDefault()`
- **非Android(Expert/PC)**: `StartupUI`(IMGUI/OnGUI) を出し `OnConfirmed` 待ち → config適用 → Connect。
- **Android(Quest/Worker)**: **ヘッドレス**＝UIなしで即進行（configはPC側で事前Save）。
- `ShowStartupPanel`(:129) は呼び出し元ゼロの**死にコード**（別途撤去候補）。

変更後:
- **非Android**: StartupUI を拡張（自己チェック＋④復元）。
- **Android**: ヘッドレスをやめ **Worker VR起動パネルを表示し、コントローラ確認で進行**（ユーザー決定）。

---

## 1. 共有: 自己チェック（DRYに1箇所）
新規 `StartupSelfCheck`（static, プレーンC#）。`List<Issue>` を返す。`Issue { Severity(Fatal/Warning/Info), string message }`。
両画面がこれを呼んで描画（IMGUI / WorldSpace）。

| 項目 | 重大度 | 判定 | 出典の危険ケース |
|---|---|---|---|
| instructions_new.txt 有無・非空 | **Fatal**（Start/確認不可） | `LoadInstructions` が読むパスを確認して存在＆行数>0 | 「instructions欠落→永久Enter無反応」 |
| SharedMesh 存在（シーン） | **Fatal** | `GameObject.Find`/`MeshHandler` 解決可否 | 「SharedMesh未設定→calib進まず」 |
| logs/P{participantId} 既存 | Warning | ディレクトリ存在 | 「同一participant再走でCSV追記汚染」 |
| rain_loop（Resources/Audio） | Warning | `Resources.Load` null判定 | 「nature音欠落で条件音量ズレ」 |
| 条件順プレビュー | Info | orderIndex から block/gaze 順を解決して文字列化 | 「participant番号誤入力で条件順崩れ」 |
| participantId 非空 | **Fatal** | trim後空でない | 誤起動防止 |

- **Fatal が1つでも → Start/確認ボタンを無効化（赤表示）**。Warning は黄表示で続行可。Info は通常表示。
- instructions/SharedMesh の正確なチェックは `LoadInstructions()` の実パス（StreamingAssets か persistentData か）を確認してから確定（**未決#1**）。

---

## 2. Desktop(Expert) = 既存 `StartupUI`(IMGUI) を拡張
IMGUI のまま（EventSystem不要・全入力モードで動く既存の利点を維持）。`OnGUI` にセクション追加。

レイアウト追加（既存の participantId/index/python/mic/offline の下）:
1. **自己チェックパネル**: `StartupSelfCheck.Run()` の各 Issue を行表示（Fatal=赤●, Warning=黄●, Info=灰）。`_panelH` を項目数で伸長。
2. **条件順プレビュー**: orderIndex を動かすと即「Block順: … / Gaze順: …」が更新（誤入力をその場で気づける）。
3. **④ 復元プロンプト**（PlayerPrefsに再開可能データがある時のみ）:
   - 「前回の続き（条件 X/10, 保存 hh:mm）から再開しますか？」＋ボタン `[復元する]` / `[最初から]`。
   - 既定=最初から。`[復元する]`選択時は `_config` にフラグを立て、Confirm時に SceneBootstrapper2 へ resume指示を渡す（④本体は別実装・別sign-off）。
4. **Startボタンの gating**: `hasFatal` なら無効（灰）＋「instructions/SharedMesh を確認してください」。

`Confirm()`: 既存に加え resume選択を config/フラグへ反映。

> Desktop は exe 化されるため、このIMGUI起動画面が**唯一のオペレータ入口**。Editorコンソール非依存で完結。

---

## 3. HMD(Worker) = 新規 `WorkerStartupPanel`（WorldSpace VR）
IMGUIはVR内に出ないため WorldSpace Canvas で新規作成（`WorkerHUD2`/`SetupCoordinator` のVRパネル流儀を踏襲）。

### 3-1. 生成・配置（UI）
- 新規 `Assets/Scripts/Core/SceneBootstrapper/WorkerStartupPanel.cs`（MonoBehaviour）。
- `OVRCameraRig.centerEyeAnchor` 子に WorldSpace Canvas（WorkerHUD2 と同様 `localScale=0.001`、前方〜1m、NotoSansJP フォント自動ロード）。
- 表示内容（縦並び）:
  - タイトル「セットアップ準備」
  - 設定サマリ: `参加者 P{id} / Python {host} / {online|offline}`
  - 接続: `接続中… / ルーム参加済(同室 n/2)`（Photon状態）
  - 自己チェック行（`StartupSelfCheck` の Worker関連項目: config/SharedMesh等。Fatal=赤）
  - 確認ヒント: 「準備ができたら **右コントローラの A ボタン** で開始」（Fatal時は「{問題}を解決してください」を赤表示しヒント抑制）

### 3-2. インタラクション（XR）— コントローラ確認
- **方式: 生 `OVRInput` ボタンのエッジ検出**（起動直後＝Canvas Raycaster/EventSystem 未整備でも確実に効く。NASA-TLXの laser/poke は OVRInputModule 依存で boot時に過剰）。
- **確認ボタン = `OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)`（右A）**。
  - グリップ(`PrimaryHandTrigger`)は Setup中のQR手動登録に使用済 → **競合回避のため起動確認には使わない**。A は MeshHandler でも確定に使用例あり＝被験者に馴染み。
  - 押下時: ハプティクス1発（`OVRInput.SetControllerVibration` 0.5/0.8, RTouch, 0.2s）＋「開始します」フラッシュ。
- **Fatal がある間は A を無視**（確認不可）。Fatal解消で確認可能。
- 代替案（不採用・記録）: VRボタン＋`QuestionnaireLaserInput`/`PokeInput`。NASA-TLXと一貫するが、起動直後にOVRInputModule/Raycaster/EventSystem一式が要る＝重い・boot順依存。startup確認のような単一アクションには生OVRInputが堅牢。

### 3-3. フロー結合
`SceneBootstrapper2.StartupFlow()` の Android分岐を変更:
```
// 旧: Androidは即進行
// 新:
var panel = <add WorkerStartupPanel>;
panel.Initialize(config);            // 自己チェック実行・表示
yield return new WaitUntil(() => panel.Confirmed);   // A押下（Fatalなし）で true
```
- 接続前に出す（config読込直後）。接続状態はパネルが Photon を見て更新。
- Fatal時は `Confirmed` にならない＝ヘッドレス無言進行で壊れる現状を防ぐ。

---

## 4. ④復元との関係
- 復元の「選ぶ」UIは **Expert StartupUI のみ**（§2-3）。Worker は復元の有無に関わらず再Setupするため選択不要。
- ④本体（PlayerPrefs保存／復元時に条件Nへ）は `DESIGN_state-persistence.md` の別sign-off。本起動画面は「resume選択フラグを Confirm 時に渡す」受け皿だけ用意。

---

## 5. 影響ファイル（合意後）
- 新規: `WorkerStartupPanel.cs`（VR起動パネル＋A確認）、`StartupSelfCheck.cs`（共有チェック）。
- 変更: `StartupUI.cs`（自己チェック行＋条件プレビュー＋④復元プロンプト＋Start gating）、`SceneBootstrapper2.StartupFlow`（Android分岐をパネル待ちに）。
- 撤去候補: 死にコード `ShowStartupPanel`(:129)。

---

## 6. 未決事項（sign-off で確定したい）
1. `instructions_new.txt` の実パス（StreamingAssets? persistentData?）— `LoadInstructions()` を確認して自己チェックの存在判定を確定。
2. SharedMesh の存在チェック方法（名前 `"SharedMesh"` 固定 or `MeshHandler.meshObjectName`）。
3. Worker確認ボタンは **A(右)** で良いか（トリガー/Xでなく）。
4. Fatalの範囲（instructions・SharedMesh・participantId空 を Fatal、それ以外Warning）で良いか。
5. 条件順プレビューの表示粒度（Block順＋Gaze順の文字列で十分か）。
6. Worker起動パネルを接続「前」に出す（接続中も表示し状態更新）で良いか。

---

## 7. 検証（実装後・必須）
- EXE(Expert): instructions を一時退避して起動→Start不可＆赤表示を確認／logs/P{id} 既存で黄警告。
- HMD(Worker): ビルドして装着→起動パネル表示・A確認で進行・Fatal時に進めないこと。
- 2クライアント: 同室n/2 表示・別room/別regionで「未接続/1人」が出ること。
