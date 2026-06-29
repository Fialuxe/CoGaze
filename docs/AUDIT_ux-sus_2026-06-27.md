# CoGaze 遠隔協調VR実験 UX/SUS 監査 2026-06-27

> 生成: `cogaze-ux-sus-audit` ワークフロー（UX Researcher ×2 でジャーニー棚卸し → 4レンズ並列評価[SUS予測/Worker空間UX/Expert操作者UX/指示明瞭性] → UX Architect が統合）。
> 役割: **Expert**(Remote / PC操作者=実験者) / **Worker**(Local / Quest3装着者=被験者)。
> 本レポートは認知ウォークスルー/ヒューリスティック評価による**予測**（実ユーザー測定値ではない。数値SUSは算出しない）。critical級の主張は一次ソースで実機挙動を確認済み。対象コミット `db7585b`。

## 1. 総括判定: 達成基準 = **no-major**（満たさない）

達成基準3項目いずれも不成立。評価の物差しは『**システムが伝える**明瞭性』（「パネルを単に出すは論外」）であり、「操作者が口頭で補えるから良い」は減点緩和にならない。現状は外部知識（`docs/OPERATOR_RUNBOOK.md`）に依存して初めて回る構造で、これ自体が基準不適合。

- **(1) 状態と表示の一致**: 体系的に不一致。Rest→Questionnaireゲート流用でExpert最上位ラベルが「アンケート中」、識別後TaskCompleteが存在しないアンケートを案内、Finishedが謝辞を出す裏でSSQ進行、NoGaze統制でも「視線で示す」。
- **(2) 実験の円滑性**: 不成立。被験者の唯一の中核操作=識別回答手段が一度も伝わらない。NASA-TLXのEnter打ち切りとFinished/SSQ早すぎる終了は**誰にも気づかれず主要測定値を破壊する沈黙のデータ欠損**。
- **(3) 指示の分かりやすさ**: 不成立。回答ジェスチャ・進行主体・較正進捗・アンケート入力手段のいずれもHMD内/操作画面で適切に伝わらない。

## 2. ⚠ 直前スプリント `db7585b` への影響（最重要・要注意）

今回の監査で、`db7585b`（実験安全スプリント）の一部が**実機で機能しない**ことが一次ソースで判明。「Androidコンパイル緑のみ・実機未検証」だった該当箇所:

- **識別=トリガー化が画面に出ない**: `WorkerHUD2.ConnectIdentificationTask`（WorkerHUD2.cs:200）の**呼出元がゼロ**（grep確認）。`Worker_QRFound`（=20cm近接＋人差し指トリガー指示）は到達不能ハンドラ内（:209）でのみ設定されるため、**被験者には一度も表示されない**。実際にHUDに出るのは存在しない「Done」ボタンを案内する旧[local]文（WorkerHUD2.cs:508-514）。→ **配線（SceneBootstrapper2で `ConnectIdentificationTask` 呼出）＋旧[local]『Done』撤去が必須**。
- **音声データ損失**: リモート音声WAVが実レート約48kHz（`OnAudioFilterRead` のDSPレート）を16000Hzヘッダで保存し再生破綻（WF1 rank2 と同根）。→ ローカルmic(16kHz)は正だがリモートは要レート整合。

## 3. 根本原因（5パターン）— 個別文字列修正より構造是正が高レバレッジ

| # | パターン | 代表箇所 | 影響 |
|---|---|---|---|
| A | **Connect*メソッド未配線**で正しい文字列がデッドコード化 | `ConnectIdentificationTask`(WorkerHUD2.cs:200)・`ConnectMeshHandler`(:80) とも呼出元ゼロ | 識別回答指示・較正段階表示・完了触覚が一切発火しない |
| B | **Questionnaireゲート状態の多目的流用 + UI側のStepType非参照** | Rest/Alignment/ConditionStart が `Transition(Questionnaire)` を共用(:445-455) | Rest=「アンケート中」、Enter打ち切り、TaskComplete誤案内 |
| C | **役割別指示の逐語パススルー（条件非適応）** | `GetCurrentInstruction/GetInstruction`(:1092-1104) が逐語返却 | NoGaze統制でExpertに「視線で示す」=統制妥当性を毀損 |
| D | **進行入力を持たない役割への能動動詞** | Worker_TaskComplete/NoiseComplete「次へ進んでください」(CoGazeStrings.cs:69-70) | 被験者が押せるものを探して固まる |
| E | **グリップ/トリガーのモダリティ分裂** | 登録=グリップ(SetupCoordinator.cs:175,188)/回答=トリガー(IdentificationTask.cs:111)/動画=グリップ | 運動記憶衝突で誤操作→無反応で詰む |

**配線レバレッジ**: SceneBootstrapper2 で `ConnectIdentificationTask` と `ConnectMeshHandler` を配線する小修正一つで、識別回答指示の表示・デュアルQR較正の段階表示・完了触覚という複数のcritical/high所見が同時に蘇生する（[local]『Done』優先の撤去を併せて要する）。

## 4. 役割別 予測ユーザビリティ状態（定性）

- **Worker（被験者）— severe**: 受動待機・呼吸ガイド・Rest文言は手番と合図が明確な好例。だが能動操作の中核=識別回答が伝わらない（A）、回答成否フィードバック皆無。SUS観点で使いやすさ・機能統合・一貫性・自信が severe、複雑さ・要サポート・習得・煩雑さ・事前学習が concern。
- **Expert（操作者）— severe**: 操作骨格（Enter確定/Del長押し/Tab/上バー進捗ドット）は一貫し足場良好。だが「指示文と実挙動の乖離」が体験を支配（識別でEnter無反応・選択ID非表示、Rest=アンケート中、Finished裏でSSQ、NoGazeで不可能な視線誘導）。一貫性・機能統合・自信が severe。

## 5. 優先順位付き修正リスト（順序原則: 沈黙でデータ破壊 > 被験者が動けない > 操作者が迷う > 表現磨き込み）

| rank | role | sev | effort | 修正 |
|---|---|---|---|---|
| 1 | both | **critical** | S | NASA-TLX の「待機」と「[Enter]完了」矛盾でEnterが回答を途中打ち切り → Enter分岐を `StepType.Questionnaire` で無効化し自動前進に一本化、hintから「[Enter]完了」削除（ExperimentManager2.cs:233-235） |
| 2 | both | **critical** | M | Finishedが謝辞を出す裏でWorkerはSSQ回答中 → SSQ待機ゲート新設、SSQ提出イベント購読後に謝辞へ（:633-637） |
| 3 | Worker | **critical** | M | 識別回答（近接＋人差し指トリガー）が伝わらない → SceneBootstrapper2で `ConnectIdentificationTask` 配線＋[local]『Done』撤去＋20cm圏内レティクル色変化/触覚ティック＋成功触覚/緑フラッシュ（WorkerHUD2.cs:200） |
| 4 | Expert | high | M | NoGaze統制でExpertに「視線で示す」 → `GetCurrentInstruction`(:1092-1097) を条件別テンプレ選択へ |
| 5 | Worker | high | S | 進行入力の無い被験者に「次へ進んでください」 → 受動形へ統一（CoGazeStrings.cs:69-70） |
| 6 | Worker | high | M | 課題指示HUDが頭部固定・左上0.7mで組立時に読めない → body/world-anchored小ラベル＋QRハイライト（WorkerHUD2.cs:332-336） |
| 7 | Worker | high | S | デュアルQR較正の段階進捗/完了触覚がデッドコード → `ConnectMeshHandler` 配線＋順序チェックリスト＋完了触覚（WorkerHUD2.cs:80; MeshHandler.cs:390-396） |
| 8 | Worker | high | M | グリップ/トリガー分裂 → ボタン統一 or 部位アイコン常時提示＋導入動画差替え |
| 9 | Worker | high | M | タスクQR手動登録が近接判定なしで生位置登録→後段20cm判定を沈黙破壊 → 閾値検証＋目視確認マーカー（SetupCoordinator.cs:184-190） |
| 10 | Expert | high | M | 識別でEnter無反応＋Worker選択マーカーID非表示 → 指示書換え＋`CompletedMarkerId` を上バー表示（要件確認: 標的事前指定の正誤判定が研究意図か） |
| 11 | Expert | high | S | Rest が最上位ラベルで「アンケート中」誤表示 → `StepType.Rest` 専用分岐で「休憩中」昇格（ExpertUI2.cs:404-411） |
| 12 | Expert | high | S | 識別後 TaskComplete が存在しないアンケートを案内 → 直前StepTypeで分岐（ExpertUI2.cs:414-423） |
| 13 | Expert | med | M | R/M/Setup承認が常時ヒントに無く発見不能 → `Expert2_BottomHint` を状態文脈化（CoGazeStrings.cs:47-48） |
| 14 | Expert | med | M | condstart/ready/interval の状態-表現不整合（IR/NoGazeでアイトラ確認、calib中Enter無音、次条件誤認） → 条件別出し分け＋能動応答 |
| 15 | both | med | M | 組立課題に完了シグナル無し → Worker口頭ハンドオフ＋Expert視点切替告知＋hint修正 |
| 16 | Worker | med | S | アンケート選択操作（タッチ/トリガー）が示されずレーザー不可視 → 第1問に操作アフォーダンス一行（QuestionnaireManager.cs:514-521） |
| 17 | both | low | S | GazeMode盲検露出/タイムアウト「!」言語化/被験者宛「再起動」分離/専門語（メッシュ）平易化 |

## 6. 検証メモ（一次ソース確認済み）
- Enterハンドラ(ExperimentManager2.cs:226-247)は Ready/TaskComplete/NoiseComplete/Questionnaire のみ作動。**TaskRunning は対象外**＝識別課題中Enterは無反応。Questionnaire状態では完了チェックなしに即 `AdvanceStep()`＝NASA-TLX打ち切り。
- Rest→`Transition(ExperimentState.Questionnaire)`(:450-455) を確認。質問紙ゲートを流用。
- `ConnectIdentificationTask`(WorkerHUD2.cs:200)/`ConnectMeshHandler`(:80) とも**呼出元ゼロ**（grep）。`Worker_QRFound` は到達不能ハンドラ内(:209)でのみ設定。
- StepType.Task(WorkerHUD2.cs:505-517) は gaze条件で[local]を優先し、コメント(:508)が当該文に「Done ボタンを押して」が含まれると自認。
- `GetCurrentInstruction/GetInstruction`(:1092-1104) は `IsExpert ? step.Instruction : step.LocalInstruction` を逐語返却＝条件別分岐なし。
