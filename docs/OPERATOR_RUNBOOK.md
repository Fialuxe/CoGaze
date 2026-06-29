# CoGaze 視線共有実験 — オペレータ・ランブック（実験中に焦らないための実用ガイド）

> 目的: 起こりうる事態を全て把握し、「今何が起きて・どう対処するか」を画面と本書で即判断できる状態にする。
> 凡例: **W:** = Worker(Quest3/VR)が見るもの / **E:** = Expert(PC)が見るもの。`対処不能（再起動 / 要再設計）` は実験中の復帰手段が本当に存在しない事態（嘘の対処を書かない方針）。
> コード確認済みの主要行は §5 に集約。

---

## 0. まず全体像 — 「これが正常」の背骨（End-to-End ハッピーパス）

異常を見抜く前提は「正常の見え方」を知ること。正常進行は以下の一本道：

```
[両者同時起動] → Photon接続(region=asia, room=CoGaze_Room)
  → Setup（両者ローカルで直接 Setup 入場・ブロードキャストされない）
      W: dual-QR(A/B)を見る → calib完了(両手振動) + タスクQRをN個検出/手動登録
      E: チェックリスト(calib✅ / タスクQR n/N) が埋まる → 承認ボタンが緑
  → [Expert: Escでカーソル解除 → 承認ボタン] → Idle（一瞬）→ Ready（Python pong or 10s後）
  → [Expert: Enter] → ConditionStart（条件1/10）
      IR/NoGaze: そのままEnter / Webcam系: キャリブPASS待ち→Enter
  → WhiteNoise(20s 呼吸ガイド) → NoiseComplete [Enter]
  → TaskRunning(Task 60s / Assembly 180s) → TaskComplete [Enter]
  → Questionnaire(NASA-TLX, Worker提出で自動前進)
  → …(条件×10 繰り返し)… → Finished → SSQ（最後の1問・外さない）
```

**正常の指標**: タスクQR数が増える / calibに✅と両手ハプティック / 承認ボタンが緑 / 条件ドットが進む / Worker「待機中」はExpert開始操作待ちの正常状態。

---

## 0.5 操作者キー・チートシート（焦りの大半はここで防げる）

| キー | 効く状態 | 効果 | 落とし穴（最重要） |
|---|---|---|---|
| **Esc** | 全般 | マウスカーソルのロック解除 | **Setupで承認ボタンを押す前に必須**。ロック中はカーソルが画面中央固定で右上の承認ボタンに届かず「緑なのに押せない=進行不能」。知らないと詰まる |
| **Enter** | Ready / TaskComplete / NoiseComplete / Questionnaire | 開始 / 1ステップ前進（500msデバウンス有） | キャリブ中(`_calibrationPending`)はブロックされ無反応。Questionnaireで早押しするとNASA-TLX欠測 |
| **Del** | TaskRunning / WhiteNoise / Questionnaire | 強制スキップ / 質問紙強制前進 | **キャリブACKデッドロックからの唯一の脱出口だが画面に案内なし**。確認ダイアログ無し・巻き戻し不可 |
| **R** | Questionnaire かつ ConditionStart かつ FAIL時のみ | キャリブ再試行 | **MARGINALでは無反応**（FAIL限定）。条件外で押しても無表示 |
| **M** | 全状態 | Worker視界に共有メッシュ表示(全クライアント) | タスク中に押すと被験者視界に重メッシュ→気が散る+GPU負荷+データ汚染。確認・ロック無し |
| **1/2/3** | TaskRunning以外 | 視線可視化モード変更 | TaskRunningのみロック。ConditionStartで誤爆すると次タスク本番に誤モード持ち越し＝サイレント条件汚染 |
| **V** | 全般 | WebRTC映像canvasトグル | Assembly中に白画面で視界が塞がれた時の視界回復手段 |
| **WASD/マウス** | 非Assembly | Exportフリールック | 動かすと共有視線原点がずれる。タスク中はデータ汚染 |
| **F2/F3/F4** | TaskRunning（Task/Assembly） | 発話エスカレーション段の記録（F2=特徴語 / F3=空間語 / F4=向き※組立のみ） | 段を上げた瞬間に押す。押し忘れ＝max_rung過小。識別はF2/F3のみ。trials.csv:max_rung＋escalations.csvに記録。詳細§1-E′ |

---

## 1. フェーズ別ランブック

### 1-A. Setup（QRキャリブ＋タスクQR登録）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常進行 | 両者同時起動 | W:「セットアップ中」+VRパネル(calib⬜/タスク0/N+「QR-A/QR-Bを見て」) E:チェックリスト+灰の承認ボタン | calib✅と全タスクQRが揃うまで待つ。Esc→承認 |
| タスクQR自動検出されない | MRUK検出むら(急角度/床コード/密集)。※「6個上限」は実機で否定済＝精度問題 | W:「未認識のQRがN個『A』にコントローラを当てて右グリップ」+振動 E:「タスクQR n/N (未: A…)」 | Workerに物理QRへコントローラを当てさせグリップ登録を口頭誘導。誤登録は上書き可だが訂正UIは弱い |
| QRは見えるがデコード失敗(NULL payload) | 床のQRを急角度で見る等 | W:何も起きない(数が増えない) E:数増えず・原因表示なし | 角度・距離を変えて見直すよう口頭指示。タスクQRなら手動登録に切替 |
| **calib用QR(A/B)が片方読めない/遮蔽** | 障害物・印刷ずれ・角度 | W:「✓QR-A→QR-Bをスキャン」のまま固着 E:calib⬜・承認灰色 | 遮蔽除去→再スキャン。**calib QRには手動代替が無い**→読めなければ `対処不能（再起動）` |
| **MRUK完全失敗(QR追跡未起動)** | MRUK.Instanceが10秒で初期化されない/権限欠落 | W:「QR-A/QR-Bを見て」のまま永久・エラー無し E:0/N・承認灰色 | ログ確認。`対処不能（再起動 / 要再設計）`。「未起動」と「未スキャン」の区別表示が無いのが根本問題 |
| **SharedMeshがシーンに無い/名前不一致** | Inspector未設定・リネーム | W:calib一切進まずグリップ無反応 E:マーカーが異常位置/calib進まず | `対処不能（再起動）`。**起動前のシーン設定確認が必須**（メモリにも「Inspector設定要」） |
| **calib完了したが位置がズレている(誤キャリブ)** | QR誤認識/A・B配置不整合 | W:「キャリブ完了」表示・本人は気づけない E:Mキーでメッシュ目視確認するとズレが見える | Mで確認。ただし**再キャリブ起動UIが未配線**(ResetDualCalibration)→`対処不能（再起動 / 要再設計）` |
| 手動登録の位置ズレ(誤登録) | コントローラをQR実位置からずれて当てる | W:成功と同じ振動・カウント増(区別なし) E:揃って見え気づけない | 同IDの再登録UIが無い→`対処不能（再起動）`。識別課題で初めて顕在化しうる |

### 1-B. Idle → Ready（承認後・開始前）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常: 承認→即Ready | calib+全QR完了で承認 | W:「待機中」(Idleミラー、Readyは見ない仕様) E:Idle一瞬→「準備完了 [Enter]開始」 | Enterで開始。Workerが「待機中」のままは正常（開始操作待ち） |
| 承認後Readyにならず停滞(最大10s) | Python pong未着 or テンプレ未読込 | W:「待機中」 E:承認したのにIdle(黄)・Enter無反応 | pong受信 or 10秒timeoutで自動Ready。**待てば直る**。今は無言なので焦るが正常 |
| **instructions_new.txt 欠落/空** | StreamingAssetsにファイル無し/0ステップ | W:「待機中」のまま E:「⚠instructions_new.txtが見つかりません」承認しても**永久にEnter無反応** | `対処不能（再起動）`。ファイル配置→再起動。起動時致命エラー化が必要 |
| **遅参加/再起動Expertが承認不能** | Workerがcalib完了→後からExpert参加/再起動 | W:「準備完了-承認待ち」(自分は正常で異常に気づけない) E:メッシュは正しい位置なのにcalib⬜・承認灰色・理由表示なし | **§2-②の最重要ケース**。`対処不能（再起動 / 要再設計）`。calib完了RPCが非buffered(:357)で取りこぼし |

### 1-C. ConditionStart（条件先頭・キャリブ）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| IR/NoGaze(キャリブ不要) | 条件先頭 | W:「【条件X/10】…」 E:「条件開始準備 [Enter]開始」+条件ラベル | Enterで開始 |
| Webcamキャリブ PASS | StartSession→ACK→キャリブ成功 | W:ConditionStart文のまま(進行表示なし) E:calib.running→「完了(PASS)[Enter]」 | キャリブ中はEnter無効(正常)。PASSでEnter |
| キャリブ MARGINAL | quality=1(自動2回リトライ後) | E:「再試行中(n回目)」→「MARGINAL 精度低め err=… [Enter]続行/[R]」 | Enterで許容続行。**RはMARGINALでは効かない**(FAIL限定) |
| キャリブ FAIL/中断 | quality=0 | E:「失敗または中断 [Enter]スキップ/[R]リトライ」 | Rで再キャリブ。Enterスキップは未キャリブ記録になる旨を意識 |
| **session_start ACKが来ない→Enter永久ブロック** | Python落ち/遅延 | W:ConditionStart文のまま固着 E:calib.running「[Enter]無効」のまま・原因表示なし | **Delで離脱可だが画面に案内なし**。タイムアウト未実装→毎Webcam条件で再発しうる |
| **/calibration/result が来ない→完了せず固着** | キャリブ途中でPython停止 | W:固着 E:「キャリブ実施中」のまま | 同上、**Delで離脱**（裏技、要周知）。AbortCalibration UIなし |
| ACKがok以外→黙ってスキップ | Pythonがok以外で応答 | W:変化なし E:Enterが通るが「キャリブしてない」明示なし | Rで再試行可能だがスキップに気づきにくい |
| **Webcamキャリブ中Workerに進行表示が無い** | 30秒級キャリブ走行中 | W:「【条件X/10】」のまま・なぜ進まないか不明 E:calib.runningで把握可 | **口頭(Voice)で「今キャリブ中」と伝える運用に依存**。Worker側フィードバック経路が無い |

### 1-D. WhiteNoise（インターバル20s）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常 | StepType.Noise | W:呼吸ガイド(拡縮ディスク+吸って/吐いて)+残り時間 E:「休憩中」+黄タイマー | 満了で自動NoiseComplete。早送りはDel |
| Del強制スキップ | 誤/意図的Del | W:呼吸ガイド終了→「インターバル終了」 E:「インターバル終了 [Enter]次へ」 | 意図的なら正常。**残り時間が多い状態のDelはノイズ曝露未達で条件前提が崩れる**ので注意 |
| nature音(rain_loop)未配置 | Resources/Audio/rain_loop無し | W:ブラウンノイズのみ(気づきにくい) E:差分なし(ログのみ) | 音量条件を厳密にするなら起動前にファイル確認 |
| タイマードリフト | フレームレート差 | W:最大10s毎に微小ジャンプ | 自動補正(PeriodicResync)。一瞬飛んでも異常でない |

### 1-E. TaskRunning（Task 60s / Assembly 180s）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常 識別課題(Task) | StepType.Task | W:タスク指示+タイマー(残5s赤/0で『!』) E:「タスク実行中」赤+タイマー | 満了でTaskComplete。早期完了も可 |
| 正常 組立課題(Assembly) | StepType.Assembly | W:組立指示+3分タイマー E:Workerカメラ追従映像 | 早期完了不可・満了 or Delで進める。両者に「3分・操作者がDelで進める」明示が望ましい |
| **Worker早期完了がTaskCompleteゲートを飛ばす(非対称)** | 正しいQR近傍で右グリップ→完了RPC | W:課題消え次のインターバルへ E:TaskComplete挟まずWhiteNoiseへ直行・確認機会なし | 仕様。止める手段なし。「Workerが完了しました」通知が無いのが驚きの元 |
| Del強制スキップ | 誤/意図的Del | W:「タスク完了」 E:「タスク完了 [Enter]次へ」 | 意図的なら正常。**確認なし・巻き戻し不可**、試行データ途中切断+OSC EndTrial送出 |
| 被験者が違うQRでグリップ→誤同定Done | 対象外QRの20cm内でグリップ | W:「QR発見」→完了で次へ・誤りの指摘なし E:自動前進・どのIDで完了か即時表示なし | 現場では検知不可(解析時CompletedMarkerIdで判定)。完了ID即表示が望ましい |
| **Assembly中Follow空振り** | Worker姿勢が見つからない(未参加/オーナーシップ未確定) | W:影響小 E:「タスク実行中」だがカメラがWorkerに追従せず・エラー表示なし | 進行中の手当てなし。`対処不能（要再設計）` |
| **被験者が試行中に右グリップ→SharedMeshが動く** | Setup外で右グリップ→スティック→A | W:_calibText較正ヒント(v2では未配線で出ない場合あり) E:マーカー/空間が突然ずれ・原因表示なし | A確定前なら再グリップで戻せるが**A後は`対処不能（再起動 / 要再設計）`**。SuppressManualCalibGripがSetup限定(:146)が根本原因 |

### 1-E′. 発話エスカレーション・プロトコル（識別/組立共通・要オペレータ訓練）

視線（NoGaze条件は音声のみ）で対象が伝わらないとき、Expertは**段階的に言葉を足す**。段を上げた瞬間に**F2/F3/F4で記録**する（押し忘れ＝その段に未達扱い＝max_rung過小に注意）。

| 段 | 内容 | 例 | 上がる条件 | キー |
|---|---|---|---|---|
| 1 | 指示語＋視線 | 「これを・そこに」 | 開始時（既定・max_rung=1） | （無し） |
| 2 | 特徴・相対語 | 「赤いQR／L字のを・ひとつ右のマスに」 | 下段で通じない | **F2** |
| 3 | 空間・座標語 | 「右上のQR／中央手前のマスに」 | さらに通じない | **F3** |
| 4 | ＋向き（**組立のみ**） | 「90度右に回して」 | 正しいマスにあるが向き違い／配置失敗が続く | **F4** |

- **組立 (Assembly, 180s)**: 各段は**無反応/誤りが約10秒続いたら次へ**上がる。識別の段1–3＋向きの段4まで使用。
- **識別 (Task, 60s)**: 早期完了（Workerがグリップで選択）か満了の早い方。**組立と同じ10秒しきい値**でOK（60秒なら段3まで十分到達可能）。**向きの段（F4）は無し**＝段1–3のみ。
- NoGaze条件も同じ段・キーで記録（視線が無いぶん早く上段になる＝高い max_rung が想定どおり）。
- **記録先**: 試行ごとの最高段＝`logs/P{n}/trials.csv` の **max_rung** 列。各押下のタイムスタンプ＝同フォルダ **escalations.csv**（`trial_id,t_ms,elapsed_s,condition_index,task_type,rung`）。押し間違いは escalations.csv の系列から事後補正可（max_rung は最高段なので過大方向に注意）。
- **方法論的意味**: 「視線がどれだけ言葉を足さずに済ませたか」の客観指標。良い視線条件ほど低段で完了し、NoGaze は高段に至るはず。理解項目が天井でも max_rung が視線の効き目を捉える。

### 1-F. TaskComplete / NoiseComplete（ゲート）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常 | 満了/Del到達 | W:「タスク完了/インターバル終了」 E:「[Enter]次へ」 | Enterで前進。次が何か(休憩/タスク/アンケート)を予告できると段取り良い |

### 1-G. Questionnaire（NASA-TLX）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常 | Worker提出 | W:NASA-TLX質問票 E:「アンケート中・完了待ち [Enter]完了」 | Worker提出で自動前進。勝手にEnterしない |
| **Expert早押しEnterで欠測** | 待ちきれず/癖でEnter | W:回答中に質問票が消える/次条件へ E:前進・欠測警告なし | `対処不能（巻き戻し不可）`。当該条件のNASA-TLX欠測。提出前Enterを無効化すべき |
| Workerフリーズ→Del緊急前進 | アンケート応答せず | W:アンケート閉じ次条件へ E:次条件へ | Delで緊急前進(欠測扱い)。ヒントが弱いので要周知 |
| 被験者が操作に迷う/未提出 | 操作ミス | W:質問紙がカメラ追従で表示継続 E:進捗(何問目)が出ず停滞 | 口頭補助→提出。詰まればDel(欠測) |

### 1-H. Finished

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 正常終端 | 最終Questionnaireを越える | W:「実験終了」+SSQ質問票 E:「実験終了/全条件終了」+ドット全緑 | **SSQが残っている**。Workerに「最後にもう1問・外さないで」と伝える |
| **SSQ提出前にHMDを外す→欠測** | 「終了」表示を完了と誤認 | W:SSQに答える前に外すと回答機会喪失 E:SSQ提出状況が画面に出ない | 再装着させ口頭で促すしかない。提出完了をもって「取り外し可」の合図を出す運用 |

### 1-X. 横断: 切断 / 再接続 / デバイス（最も焦るゾーン）

| 事態 | 引き金 | 何が見える | 操作者の対処 |
|---|---|---|---|
| 実行中(Task/Noise)にWorker再接続 | Wi-Fi瞬断等 | W:一瞬「セットアップ中」→最大10sで復帰 E:Photonピア表示が無く気づけない | 待てば自動復帰(≤10s)。「再同期中」表示が無いので焦るが正常 |
| **ゲート状態中にWorker再接続→「セットアップ中」で固着** | Idle/Ready/Complete/Questionnaire中の切断 | W:「セットアップ中…」で動かない(calib未完の偽表示) E:Worker固着を観測できず相互待ちデッドロック | **裏技: ExpertがEnter/Delで1段進めると新broadcastで復帰**。PeriodicResyncがゲート状態では回らない(:700)+pull同期が死(:684)が根本 |
| **HMD一時取り外し→無自覚な切断連鎖** | 汗拭き・フィット調整でHMDを数秒外す→OSがアプリ停止→切断 | W:被り直すと「セットアップ中」へ巻き戻り・理由なし E:ピア状態表示が無く「急に反応しない」を切り分け不能 | **§2-①最重要**。`対処不能（要再設計: OnApplicationPause未実装(grep0件)）`。被験者教示で「外す時は声をかけて」 |
| **Worker再接続後WebRTC映像が復旧しない** | 再接続後カメラ映像が固まる | W:表示なし E:映像が最後のフレームで静止/黒。Assemblyで作業が見えない | `対処不能（要再設計: _offerTriggered居残りで再オファされない）`。実質Workerアプリ再起動 |
| Worker再接続後の音声非対称 | 再接続後の双方向音声 | W/E:相手の声が復帰しないことがある・表示なし | `対処不能（UI導線なし）`。多くは待機/再起動で回復 |
| **Expert切断→再接続で実験位置が全消失** | Expert(単一権威)の瞬断/再起動 | W:Expert再Setup後に「セットアップ」が降ってきて巻き戻り E:「セットアップ」から再開・進行済み条件が全消失 | `対処不能（要再設計: CurrentStepIndex永続化なし）`。実質セッション破棄 |
| **Expert切断中にWorkerタイマー完走→固着** | Task/Noise中にExpert切断 | W:タイマー00:00で止まりTaskRunning表示のまま・アラート出続け E:自分が切断・固着を知る術なし | `対処不能（Expert復帰でSetup巻き戻り）` |
| **region/room不一致→永遠に相手を見つけない** | 片方のAppSettingsがasia以外/別AppId | W:「実験者待機中」(黄)のまま永遠・自分は接続済で赤にならない E:Worker在席表示なし・承認進まず | `対処不能（不一致検知なし）`。設定見直し+再起動。「同室1/2」表示が必要 |
| 致命的切断要因で無音停止 | InvalidAuthentication/ApplicationQuit等 | W:接続行が赤のまま(再接続しない) E:通知なし | `対処不能（自動再接続対象外）`。再起動・認証確認 |
| OnJoinRoomFailed握り潰し | 満室・閉室・サーバ事情 | W:「接続確認中」付近で停止 E:黒い初期画面のまま | `対処不能（自動リトライなし）`。再起動 |
| **両者切断→ルーム消滅でQR/mesh全消失** | ルータ再起動等で同時切断 | W:Setupから完全再セットアップ要求・理由なし E:承認ゲートがリセットされたように見える | `対処不能（EmptyRoomTtl=0で即破棄）`。最初からやり直し |
| **Workerコントローラ電池切れ/BT切断→全入力が無言停止** | 長時間セッションで電池消耗 | W:グリップしても振動もカウントもなし・「押してるのに進まない」 E:タスクマーカー増えない/早期Done来ない・原因表示なし | `対処不能（電池/接続監視なし）`。電池交換/再ペアリング。切り分け表示が無いのが脆弱性 |
| **Expertフォーカス喪失/PCスリープ** | Python端末へalt-tab/離席でロック | W:状態が進まず固着 E:ゲーム画面が背面・Enter/Delが届かず「押したのに進まない」 | Unityウィンドウを再フォーカスで入力復帰。スリープ起因なら復帰後に巻き戻りリスク |
| 同一participant再走でCSV追記汚染 | 再起動/取り直しで同番号再走 | W/E:画面上は正常・解析時に初めて重複発覚 | `対処不能（実行中防止なし）`。研究妥当性の静かな事故。起動前にlogs/P{番号}存在確認 |
| participant番号誤入力で条件順全崩れ | 起動前StartupUIで誤入力 | W:通常進行(気づけない) E:誤indexでも正常に見える | `対処不能（途中修正なし）`。起動時に条件順プレビュー+確認が必要 |

---

## 2. 危険ケースTOP（優先対応） — 無フィードバック/復帰手段なし順

> 共通の根本原因（§2末尾「ルート分析」参照）を意識すると、多数の症状が少数の設計欠陥に収斂する。

**① HMD一時取り外しによる無自覚な切断連鎖** 〔unhandled / high / 頻度高〕
- なぜ焦るか: 汗拭き・フィット調整という最も頻繁で「異常と思われない」物理動作が、`OnApplicationPause`未実装(Assets/Scripts内0件)+Android で runInBackground 無効のため、Setup巻き戻り/ゲート固着/映像復旧せずの最悪系へ無言で落ちる。Expertにはピア状態表示が無く帰属不能。頻度高×不可視×復帰手段なしの三拍子。
- 最小の手当て: `OnApplicationPause(false)`復帰時に Worker→Expert へ状態再送要求（既存 `BroadcastCurrentState()` を起動）。Workerに「一時停止検知・再同期中」、Expertに peer 一時停止/復帰インジケータ。被験者教示で取り外し時の声かけ徹底。

**② 遅参加/再起動Expertがキャリブ完了RPCを取りこぼし承認不能** 〔unhandled / high〕
- なぜ焦るか: メッシュ位置(AllBuffered:267)は正しく出ているのに calib完了通知だけ非buffered(`RpcTarget.All`:357)で欠落し、承認ボタンが永久に灰色。Workerは「準備完了」表示で自分が正常なため異常に気づけない。`ResetDualCalibration()`(:369)はどのUIにも未配線で復帰不能。
- 最小の手当て: calib完了を AllBuffered か Room/Player CustomProperty に永続化し遅参加でも復元。最低限 Expert に「calib強制承認」オーバーライドと「なぜ押せないか(calib未受信)」の理由表示。

**③ Expert切断→再接続で実験位置が全消失** 〔unhandled / high〕
- なぜ焦るか: Expertが単一権威で進捗(CurrentStepIndex/条件順)が永続化されず、Initialize が無条件に `CurrentState=Setup`(:113)。再接続=条件1から完全やり直し。被験者の長時間が無駄になる。
- 最小の手当て: 進捗を PlayerPrefs か Room CustomProperty に逐次保存し、再接続時に「直前状態に戻しますか?」復元導線。最優先の堅牢化対象。

**④ Workerコントローラ電池切れ/切断で全入力が無言停止** 〔unhandled / high / 頻度高〕
- なぜ焦るか: グリップ/ボタンが手動QR登録・dual-QRキャリブ・タスクDoneの唯一の入力導線。残量/接続UIが無く、入力が死んでも「押してるのに進まない」としか分からず操作者も切り分け不能。
- 最小の手当て: コントローラ接続/電池残量を Worker HUD と Expert に最小表示。入力途絶+未トラッキング検知で「コントローラを確認」提示。

**⑤ region/room不一致で永遠に相手を見つけない** 〔unhandled / high〕
- なぜ焦るか: 接続自体は成功するため例外もログ警告も出ず、Workerは「実験者待機中」(黄)のまま永遠、Expertは在席表示すら無い。設定ミスと気づくのに時間がかかる。
- 最小の手当て: 接続後「同室人数 1/2」と region/room 名を両画面に常時表示。1人のまま=別室を即検知。

**⑥ ゲート状態中の再接続で「セットアップ中」固着** 〔unhandled / high〕
- なぜ焦るか: PeriodicResyncが実行中状態限定(:700)、pull同期が死(:684)、broadcastが非buffered(:664)のため、ゲート(Idle/Ready/Complete/Questionnaire)中に再接続するとExpertが次に動くまで固着。相互待ちデッドロックになりうる。
- 最小の手当て: Expert に Worker接続/状態の生存表示と「現在状態を再送」ボタン(`BroadcastCurrentState()`:668 は既に public)。全状態対象の低頻度ハートビート再送。Workerに「復帰待ち」明示。

**⑦ Webcam条件で session_start ACK が来ずキャリブ無期限ブロック** 〔partial / high / 毎Webcam条件で再発〕
- なぜ焦るか: ACKにタイムアウトが無く、`_calibrationPending`が永久にtrue→Enter永久ブロック。「キャリブ実施中」表示が停止を示さない。唯一の脱出口Delが画面に案内されていない。
- 最小の手当て: ACK待ち/結果待ちにタイムアウト(例10s)→「Python応答なし — [Del]でスキップ」を明示。Pythonドット(NG)と関連表示。

**⑧ 実験中の右グリップでSharedMeshが動く（再キャリブ暴発）** 〔unhandled / high〕
- なぜ焦るか: `SuppressManualCalibGrip`がSetup時のみtrue(:146)。Setup離脱後はグリップで較正モードON→スティックでメッシュ移動→Aで全クライアントに再配置。タスク完了グリップが較正もトグルする多重割当。v2ではConnectMeshHandler未配線で本人に較正モードの自覚すら無い。
- 最小の手当て: 試行中(Setup以外)はグリップ較正トグルを無効化。グリップ機能を状態で排他化(タスク中=完了専用/Setup=登録専用)。

**⑨ Del/Enter誤爆でデータ破壊**（試行強制終了/NASA-TLX破棄/未キャリブ続行） 〔unhandled / high〕
- なぜ焦るか: 破壊操作にデバウンス・確認・巻き戻しが無い(デバウンスはEnterのみ)。一瞬の誤爆で試行/アンケートが欠測。
- 最小の手当て: 破壊系キーに確認(長押し/二段)か直後Undo猶予。「強制終了しました/欠測」のトースト+ログフラグ。

> **ルート分析（多数の症状の背後にある少数の根）**:
> 1. **状態永続化が無い** → Expert再接続全消失・同番号CSV汚染。
> 2. **非buffered broadcast + pull同期が死(:684)** → ゲート再接続固着・取りこぼし遷移。
> 3. **calib完了が非buffered送信(:357)** → 遅参加Expert承認不能。
> 4. **グリップ較正がSetup外で無防備(:146)** → 実験中のSharedMesh暴発。
> 5. **デバイス/OSライフサイクル未捕捉(OnApplicationPause 0件)** → HMD doff連鎖・PCスリープ凍結。
> 6. **接続状態の可視化欠如(Exportにピア表示なし)** → 上記すべての「帰属不能」を増幅。

---

## 3. UI要件（増築でなく再設計を前提）

各要件に「閉じるギャップ」を併記。

### 共通（両ロール）
1. **ピア接続状態の常時可視化**: Photon接続/相手在席/最終受信時刻。Expert側に皆無(ExpertUI2はPython OSCドットのみ・OnPlayerLeftRoom未実装)。→ ⑤⑥①を帰属可能にする。
2. **「同室人数 1/2」+ region/room 名**: 別室・別AppId を即検知。→ ⑤。
3. **音声リンク状態(Expert→Worker / Worker→Expert)の最小表示(VU/接続済)**: 無音が機材かネットか口頭が無いのか切り分け。→ Voice不通が現状完全に無表示。
4. **WebRTC映像ストリーム状態**: 「接続中/映像なし」プレースホルダ(白画面の代わり)+手動再接続。→ Assembly白画面・再オファ未発火。
5. **デバイス状態**: コントローラ接続/電池残量、HMD装着検知。→ ④①。

### Expert(desktop)
6. **「進めない理由」の明示**: 承認ボタン無効時に「何が足りないか(calib未受信/タスクQR n/N/Python応答待ち残り秒)」。→ ②⑦、Idle停滞。
7. **再キャリブ要求ボタン(ResetDualCalibration RPC)**: 現在未配線。→ ②⑧、誤キャリブ訂正。
8. **「現在状態を再送」ボタン(BroadcastCurrentState)** + Worker現在状態のミラー表示(乖離検知)。→ ⑥取りこぼし遷移。
9. **進捗永続化と復元UI**: 「再接続検知→直前状態に戻す」。→ ③。
10. **破壊キーの確認/状態ロック**: Del/M/1-3 に確認 or 状態ロック。可視化モードロックを ConditionStart〜タスク間まで拡張。→ ⑧⑨、サイレント条件汚染。
11. **Setup中のカーソル自動解除** または Enterで承認可能化 + 「Escでマウス解除」常時ヒント。→ 「緑なのに押せない」進行不能。
12. **完了IDの即時表示**: 識別課題完了時に選択markerId、期待ターゲットと不一致なら警告。→ 誤同定Done。
13. **Worker質問紙進捗(n/N項目)のミラー** + 提出前Enterのブロック/確認。→ NASA-TLX欠測。
14. **起動時セルフチェック**: instructions欠落・SharedMesh欠落・rain_loop欠落・既存logs/P{番号}・participant順プレビューを画面警告。→ テンプレ/シーン設定/CSV汚染/番号誤入力。
15. **非アクティブ警告オーバーレイ** + 長尺ステップ中のスリープ抑止。→ Expertフォーカス喪失。

### Worker(VR)
16. **「QRトラッカー未起動(できない)」と「未スキャン(やってない)」の区別表示** + タイムアウト警告。→ MRUK完全失敗。
17. **「QR検出したがID読めず — 角度を変えて」**。→ NULLペイロード。
18. **calib QRの手動フォールバック** + 「A/Bどちらが未検出か」。→ calib QR遮蔽で詰む。
19. **登録済みID一覧から再登録/解除**。→ 誤登録訂正不可。
20. **Webcamキャリブ中「視線キャリブ中・そのままお待ちください」+簡易プログレス**。→ Worker無フィードバックの不安。
21. **「再同期中…(最大10秒)」「実験者の開始操作待ち」「一時停止検知・再同期します」** の中立メッセージ。→ 巻き戻り/Idle待ち/HMD doffの不安。
22. **接続インジケータを全状態で常時可視**(呼吸ガイド中も小ドット)。→ WhiteNoise中に接続表示が消える。
23. **較正モードON表示**(誤グリップを本人に知らせる、ConnectMeshHandler配線是正)。→ ⑧。
24. **Finished「最後のアンケートが残っています—外さないで」**強調。→ SSQ欠測。
25. **タスク完了操作の指示と実体の一致**（「Doneボタン」表記だが実体はグリップ+QR近接）。

---

## 4. デッドコード / 紛らわしさ（認知負荷の元）

1. **`ResetDualCalibration()`(MeshHandler.cs:369)**: 定義のみ・呼び出し元ゼロ(grep確認)。再キャリブの本来の復帰手段が「実装済みだが起動できない」状態。②⑧の復帰不能の直接原因。
2. **`DelaySyncRequest()`/`SendSyncRequest()` pull型リシンク(:677-688/:670)**: `if (CurrentState != Idle) yield break`(:684) だが Worker は Initialize で `Setup` 直接代入(:113)し承認まで Idle にならない→**初回で必ずbreakする死にコード**。SendSyncRequestに他の呼び出し元なし。⑥固着の根本。
3. **StepType.Alignment / Launch + ExpertUI2 のAlignment用UI**: 現行テンプレ(instructions_new.txt)は noise/task/assembly/questionnaire のみ生成し、Alignment/Launch経路は到達不能。未使用UIが残存。テンプレ変更時は要再検証。
4. **Idle の「Pythonプロセス起動確認」待機文言**: pongはSetup入場時(:170)に既に解決済のため Idle 経由は実質1フレームで表示されない。実態とずれた文言。
5. **v2で `hud.ConnectMeshHandler` 未配線**(LocalWorkerSetup:117 と対照): Worker HUDに較正フィードバックが出ず、⑧で自分が較正モードに入った自覚を奪う。
6. **「MRUKは~6個まで」コメント(SetupCoordinator)**: 実機で7個読めてハード上限説は否定済(プロジェクトメモリ)。誤前提のメッセージを出さないこと。
7. **Enter と Del の非対称**(Questionnaire): Del は `Hide()`してから前進(:262)、Enter は `Hide()`を呼ばず直接前進(:238)。質問紙が残るかの挙動差が混乱源。

---

## 5. 確信度と残る不確実性

### コードで確認済み（高確信）
- Worker/Expert とも Initialize で `CurrentState=Setup` 直接代入・ブロードキャストされない（ExperimentManager2.cs:113）。
- `RPC_NotifyCalibComplete` は `RpcTarget.All`=非buffered（MeshHandler.cs:357）。`RPC_ReceiveMeshTransform` は AllBuffered（:267）=メッシュは届くがcalib完了は届かない非対称。
- `ResetDualCalibration()` 定義のみ・呼び出し元ゼロ（MeshHandler.cs:369、grep確認）。
- pull型リシンクは死にコード：`DelaySyncRequest` の `if (CurrentState != Idle) yield break`（:684）+ Worker は Setup のまま。
- `PeriodicResync` は TaskRunning/WhiteNoise 限定で再送（:700-705）。ゲート状態には安全網なし。
- BroadcastState は Receivers=Others・非buffered（:664）。
- OnEvent で Ready を return で除外（:724）=Workerは Ready をミラーしない。
- Enter は `_calibrationPending` でブロック（:231）、Del は Questionnaire で `Hide()`+`AdvanceStep`（:259-264）=キャリブデッドロックの唯一の脱出口。R は FAIL かつ ConditionStart 限定（:245-247）。
- `SuppressManualCalibGrip` は state==Setup のときのみ true（SetupCoordinator.cs:146 / MeshHandler.cs:167）=Setup外は較正グリップが無防備。
- `OnApplicationPause`/`OnApplicationFocus` は Assets/Scripts に**1件も無い**（grep確認、Replay系を除く）。
- Enter デバウンス500ms（:220）、debounceはEnterのみ（Del/M/1-3になし）。

### 要実機検証（入力の推測フラグを維持・格上げしない）
- **OS のバックグラウンド/サスペンド閾値と Photon KeepAlive 猶予**: HMD doff からどれだけで切断に至るか〔推測〕。
- **リセンタ(Metaボタン)による空間整合崩壊**: 本リポジトリに該当処理なし、下流挙動は未確認〔推測〕。
- **HMDずれによる視線較正劣化**: HMD未装着検出/通知の実装は見当たらず〔推測〕。較正品質の問題であり①の権威同期問題とは別事象。
- **マスタークライアント移譲**: `OnMasterClientSwitched` 未実装（grep0件）。権威=Expert固定ロジックなので概ね無害と思われるが厳密検証は未実施〔推測〕。
- **WebRTCオファー競合**（expertReady と _setupDone のタイミング窓）: 両経路+冪等化で概ね一回化だが取りこぼし窓の有無は実測要〔推測〕。
- **コントローラ電池/接続監視の不在**: 実装無しと推定だが OVRInput 周辺の専用監視を全数確認したわけではない（grep水準）。
- **NASA提出RPCとEnterの同フレーム発火順**でConditionStartを飛ばす恐れ: ガードの隙間は実在するがPUN RPCとUpdateの発火順は要実測〔推測〕。
- **WebRTC白画面が視界中央をどれだけ塞ぐか**（canvas sortingOrder=5 と zoneB自動非表示の関係）〔推測〕。
- **二重Expert（PC2台）**: DetectRole は非AndroidをExpert扱い、多重Expert検知なし。構成管理の問題で実害は構成依存〔推測〕。

> 関連ファイル（root: C:/Users/mtaku/cogaze）: Assets/Scripts/Experiment/ExperimentManager2.cs, Assets/Scripts/UI/SetupCoordinator.cs, Assets/Scripts/Handlers/MeshHandler.cs, Assets/Scripts/QR/QRSpatialManager.cs, Assets/Scripts/Core/SceneBootstrapper/SceneBootstrapper2.cs, Assets/Scripts/Experiment/ExpertUI2.cs, Assets/Scripts/UI/WorkerHUD2.cs