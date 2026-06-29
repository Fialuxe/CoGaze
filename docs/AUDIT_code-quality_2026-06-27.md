# CoGaze コード品質監査（可読性・一貫性・可用性・堅牢性）2026-06-27

> 生成: `cogaze-code-quality-audit` ワークフロー（Code Reviewer ×9 並列レビュー → high/critical を Code Reviewer で反証検証 → Software Architect が統合）。
> 対象コミット: `db7585b`。検証済み確定 finding 数: **74**。読み取り専用監査。

## 1. 総合判定: **not-stable-major-gaps**（安定していない・重大ギャップ）

可読性・一貫性は「規約はあるが drift がある **fair**」水準。だが達成基準「全体コードの可読性・一貫性・可用性・堅牢性が安定した状態」には未到達。理由:
- **ライブセッション経路に、実機セッション中に復帰不能・サイレントにデータを失う検証済み高重大度欠陥が複数残存**。
- **既知の重大可用性ギャップ B2/B3/B6 が未実装のまま**（別途「設計重として保留」決定済み）。

判定は「保留中 B2/B3/B6（未実装前提）＋ 新規コードレベル発見」の総合。

## 2. 前提: 設計重として保留中の B2/B3/B6（優先度0・別枠）

新発見ではなく、既に保留決定済みの未実装ギャップ。可用性判定はこれらが**未実装である前提**で織り込み済み。

- **B2**: OnApplicationPause/Focus/Quit が全スクリプトでゼロ → HMD 脱着での復帰が無い。
- **B3**: 状態永続化なし＋再接続復帰なし → 途中落ちで10条件を最初からやり直し。
- **B6**: DelaySyncRequest の Worker 発リシンクが Idle ガードで通らず死んでいる。

新規発見 rank5（再接続経路の冪等化）は B3 への部分的な頭金だが、B3 が要求する「条件進捗の永続化＋途中再開」までは埋めない。

## 3. 4軸サマリ

| 軸 | 状態 | 要点 |
|---|---|---|
| 可用性 availability | **poor** | 保留 B2/B3/B6 ＋ キャリブ・ハンドシェイクのデッドロック、音声データ損失2件、WebRTC 再接続不能、OSC 視線 stale。復帰手段・operator 通知が無い。 |
| 堅牢性 robustness | **fair** | 致命ではないが『サイレント・フォールバック / default 無し / 未検証キャスト』が全域反復。実験を壊す部分集合（視線aspect、識別ターゲット誤照合、Dual-QR、NASA-TLX 写像、VRポインタ再入）あり。 |
| 一貫性 consistency | **fair** | V1/V2 残渣と多元的真実源で広く drift。UI文言三系統、QR_CALIB 二重定義、doc と実体の乖離、コメント言語/ガード/破棄パターン不揃い。 |
| 可読性 readability | **fair** | 765行ゴッドクラス（SceneBootstrapper2）、多責務 ConditionStart、死蔵コード、名称と責務の不一致、文字化け。 |

## 4. ライブ経路の検証済み致命的発見（最優先群）

| # | 内容 | 位置 |
|---|---|---|
| 1 | キャリブ・ハンドシェイク(`_calibrationPending`)にタイムアウト/失敗復帰が無く、ACK/結果が来なければ実験が無音デッドロック | ExperimentManager2.cs:486 |
| 2 | リモート音声WAVを実レート約48kHzを16000Hzヘッダで保存し再生破綻＝研究データ損失 | VoiceRecorder.cs:148-167 + RemoteAudioCapture.cs:21-39 |
| 3 | マイク開始失敗時に再取得が無くローカル音声が全実験で無音のまま静かに失われる | VoiceRecorder.cs:71-107 |
| 4 | Photon 再接続後の後始末が非冪等で WebRTC 映像が再確立されず視線キーロックも解除 | SceneBootstrapper2.cs:669 |
| 5 | Identification 条件で再構成カメラ aspect 未設定により水平視線座標がズレる | GazeVisualizer.cs:160-168 |

いずれも「落ちない・復帰できる・データを失わない」の中核を直撃し、operator が気づく通知も復帰手段も無い。

## 5. 横断的な一貫性/アーキテクチャ問題

1. **クロスプロセス・ハンドシェイクにタイムアウト/失敗復帰が無い**（可用性の最大テーマ）。OSC/Photon/WebRTC をまたぐ非同期待ちがハッピーパス前提。B2/B3/B6 もこの延長線上。
2. **サイレント・フォールバック / default 欠如 / 未検証キャストの常態化**。失敗が不可視化し誤データ・無音スタールを生む。
3. **非冪等なセットアップ / ライフサイクル非対称**。再接続・再表示で状態破壊や native/Managed リーク。
4. **位置インデックス/列挙順への依存**（名前・キーで束ねていない）。ラベル変更・近接配置で無言の取り違え。
5. **真実源の重複・doc と実装の乖離**。設定値・文言が複数箇所に散り、doc が実体と矛盾。
6. **データ損失イベントの永続記録ギャップ**。研究データを失う失敗が Debug 止まりで事後検知不能。

## 6. 優先修正リスト（rank 順 = 実験を壊す可用性/堅牢性 > 一貫性 > 可読性）

| rank | 軸 | sev | effort | 修正 | 位置 |
|---|---|---|---|---|---|
| 1 | avail | high | M | キャリブ・ハンドシェイクにタイムアウト＋失敗復帰メッセージ（session_start ACK / calibration result 両方） | ExperimentManager2.cs:486 |
| 2 | avail | high | M | リモート音声WAVを実キャプチャレートで保存（ヘッダ sampleRate/byteRate 一致） | VoiceRecorder.cs:148-167 |
| 3 | avail | high | M | マイク開始失敗のリトライ＋録音状態監視＋失敗通知 | VoiceRecorder.cs:71-107 |
| 4 | robust | high | S | Identification で再構成カメラ aspect を明示設定（EXPERT_CAMERA_ASPECT 定数化） | GazeVisualizer.cs:160-168 |
| 5 | avail | high | L | 再接続経路の冪等化（状態リセット＋再バインド、映像オファー再送、ロック即時初期化） | SceneBootstrapper2.cs:669 |
| 6 | robust | med | S | CompleteTask を最近傍マーカー採用＋期待ターゲット markerId 照合に変更 | IdentificationTask.cs:125 |
| 7 | robust | med | M | Dual-QR 較正に安定化/外れ値ガード＋到達可能な再較正トリガ（ResetDualCalibration を[PunRPC]化） | MeshHandler.cs:376,408 |
| 8 | avail | med | S | アンケートのアトミック保存（File.Replace 等） | QuestionnaireManager.cs:704-707 |
| 9 | robust | med | S | NASA-TLX のラベル→出力フィールド写像を単一ソース駆動化＋長さ assert | QuestionnaireManager.cs:640-648 |
| 10 | robust | med | M | VRポインタ suspend/restore 再入安全化＋Submit の冪等ガード | QuestionnaireManager.cs:396-407,632-675 |
| 11 | robust | med | S | 保存成否・音声断・視線断を永続ログ(FileLogger)と operator UI へ出力 | QuestionnaireManager.cs:710-713 |
| 12 | avail | med | S | OscGazeInput.IsAvailable を最終受信時刻ベースのタイムアウト判定にし視線断を通知 | OscGazeInput.cs:44-67 |
| 13 | robust | med | S | 状態機械・HUD の switch に default を追加（未処理 StepType を記録し安全遷移） | ExperimentManager2.cs:418; WorkerHUD2.cs:499-545 |
| 14 | avail | med | M | WebRTC の answerer/offerer にタイムアウト＋再試行、MediaStream を Dispose、ICE restart の再オファー実装 | WebRtcVideoSession.cs:103/112/199/295 |
| 15 | avail | med | M | WAV書き出しのワーカースレッド化＋一括書込、SaveSession ダブルバッファ化、初期確保縮小 | VoiceRecorder.cs:19-30,122-123,148-167 |
| 16 | robust | med | S | WebRTC OnEvent の CustomData を null/Length 検証してから添字参照 | SceneBootstrapper2.cs:457 |
| 17 | robust | med | S | アイトラッキング喪失時に stale 視線を送らず blink=1 相当へフォールバック | MetaXRGazeInput.cs:30-34; GazeHandler.cs:34-43 |
| 18 | robust | med | S | Python接続インジケータの猶予を ping 開始基準に変更（Time.time 絶対値をやめる） | ExpertUI2.cs:556-558 |
| 19 | robust | med | S | セットアップ表示の絵文字を同梱フォント収録記号へ置換（豆腐回避） | SetupCoordinator.cs:213-257 |
| 20 | consist | med | S | 較正接頭辞 QR_CALIB を共有定数/設定へ一元化 | IdentificationTask.cs:66 |
| 21 | consist | med | M | UI文言を一系統へ統一＋Rest 見出しの専用ケース追加 | SetupCoordinator/ExpertUI2.cs |
| 22 | consist | med | S | 死蔵コードと偽doc の解消（WorkerHandBroadcaster / ResetDualCalibration / EVT_HANGUP / FrustumVisualizer.OnDestroy） | 複数 |
| 23 | read | med | L | SceneBootstrapper2 のゴッドクラス分割＋ConditionStart のメソッド抽出 | SceneBootstrapper2.cs:28; ExperimentManager2.cs:457 |
| 24 | read | low | S | 命名・契約整理（ConnectionHandler→ExpertViewController、participantNumber、ログ接頭辞、文字化け） | 複数 |
| 25 | read | low | S | 死にコード/毎フレーム探索の除去（_qrScanned、GazeVisualizer.expManager 等） | 複数 |
| 26 | avail | med | S | オフラインツールの底打ち（FileLogger I/O 例外ガード、ReplayLoader 世代トークン） | FileLogger.cs:21,35; ReplayLoader.cs:82-108 |

## 7. 安定到達の条件（達成基準とのギャップ）

1. **前提**: 保留中 B2/B3/B6 の解消。これが無い限り可用性は poor のまま。
2. **加えて**: 上記フェーズA・B（rank1-19、ライブ経路のデッドロック/データ損失/堅牢性）。特に rank1-5 は単独でも major 判定を支える検証済み欠陥。
3. フェーズC・D（rank20-25、一貫性/可読性）で fair → good、横断問題1-6 を根治（タイムアウト規約・default 規約・冪等 setup・名前付きキー・単一真実源・永続ログ）。

**双方が揃って初めて stable**。現状は前提3点が未実装、かつ新規の検証済みデータ損失/デッドロックが重なるため **not-stable-major-gaps**。
