# 設計メモ（合意用ドラフト）: 状態永続化＋再接続復帰

> ステータス: **提案・未承認・未実装**。コード着手前にこのメモへの sign-off が必要。
> 対象: オペレータ Runbook 危険TOP #3「Expert切断/再起動→実験位置が全消失」(根本原因 #1/#3)。
> 検証前提: 実装後も「コンパイル確認済・**実機未検証**」。検証は Quest-Worker + Expert(standalone exe) の2クライアント実走が必須。

## 1. 問題（コードで確認済み）
- `ExperimentManager2` は scene 常駐の単一インスタンス。Expert が単一権威。
- 再接続/再起動のたびに `Initialize()` が **無条件で `CurrentState = ExperimentState.Setup`**(`ExperimentManager2.cs:113`)。
- `CurrentStepIndex` / 条件順位置を永続化していない → **Expert を落とす＝条件1から完全やり直し**。長時間セッションの被験者拘束が無駄になる。
- Room Custom Property は使えない: `EmptyRoomTtl=0` で**部屋が空になると即破棄**＝守りたい当のケース（Expert再起動で部屋が一時的に空）で消える。

## 2. 保存先: **Expert PC の PlayerPrefs**
- 理由: Expert は standalone exe 化予定。PlayerPrefs は OS側（Win=レジストリ）に**プロセス/部屋の生死と無関係に永続** → exe 再起動をまたいで残る唯一の現実的ストア。
- キー例: `cogaze.resume.participant`(int), `cogaze.resume.stepIndex`(int), `cogaze.resume.savedAtUnix`(long), `cogaze.resume.appBuild`(string)。
- 1参加者=1スロット（participantNumber で照合）。

## 3. 何を保存するか
| 項目 | 用途 |
|---|---|
| participantNumber | 復元照合（別参加者の取り違え防止） |
| CurrentStepIndex | 復元の核 |
| （派生）条件run位置 | 表示・確認用（stepIndexから再計算でも可） |
| savedAt(unix) | stale判定（例: 数時間以上前なら復元提案しない） |
| appBuild/version | ビルド違いの復元拒否 |

- **書き込みタイミング**: Expert側 `Transition()` 毎に `PlayerPrefs.SetInt + Save()`。頻度は低い（ステップ遷移時のみ）ので I/O 問題なし。

## 4. 復元のセマンティクス（最重要・要合意）
復元は**自動にしない**。`Initialize → Setup` を既定パスとして維持し、**復元は opt-in（オペレータが画面で確認）**。

理由（advisor指摘）: `CurrentStepIndex` を戻しても **OSCセッション・キャリブ・WorkerのQR状態は戻らない**。素朴に「ステップNから再開」すると、状態機械だけが先行し subsystem が初期状態という不整合になる。

### 提案する復元フロー（案A・推奨）
1. Expert起動 → 保存スロット検出（同一participant・非stale・同build）。
2. 画面に提案: 「前回の続き（条件 X/10）から再開しますか？ [復元] / [最初から]」。**既定は最初から**。
3. [復元]選択時も **Setup（dual-QRキャリブ＋タスクQR＋承認）は通常どおり実施**。つまり復元＝「**再Setup後に条件Nへスキップ**」であって「タスク途中から再開」ではない。
4. 承認後、状態機械を `CurrentStepIndex = 保存値` にセットして当該条件先頭（ConditionStart）から実行。OSCは当該条件のtrackerで `StartSession` を再実行。

### 却下案（記録）
- 案B「タスク途中フレームから完全再開」: OSC/calib/QR/映像の途中状態まで復元する必要があり、コスト・破綻リスクが過大。研究運用上も「中断した条件はやり直す」方が妥当。→ **不採用**。

## 5. エッジケース
- participant不一致 → 復元提案を出さない（取り違え防止）。
- stale（古い保存） → 「最初から」既定、復元は出すが警告色。
- build不一致 → 復元拒否。
- Workerも落ちている → 復元してもSetupからなので問題なし（再calib含む）。
- 1条件完了ごとに保存 → 途中条件は「その条件の頭」までしか戻らない（タスク途中は戻らない＝仕様）。

## 6. 未決事項（sign-off で確定したい）
1. 「stale」の閾値（例: 6時間？セッション当日のみ？）。
2. 復元粒度は「条件先頭」で良いか（タスク/インターバル単位は不要か）。
3. OSC `StartSession` 再実行で Python 側が二重セッションにならないか（Python仕様の確認・要実機）。
4. 保存はExpertのみ。Worker側に保存は不要との理解で良いか。
5. 復元提案UIの置き場（StartupUI? Expert承認パネル?）。

## 7. 実装範囲（合意後）
- `ExperimentManager2`: 保存(`Transition`内)＋起動時ロード＋`ResumeFromSaved(stepIndex)` API。
- 復元提案UI（小）。
- `Initialize` の既定は不変（Setup）。復元は明示パスのみ。
- 影響範囲が権威状態機械に及ぶため、**2クライアント実機で「Expert exe 再起動→復元→条件N再開」を必ず実走確認**。
