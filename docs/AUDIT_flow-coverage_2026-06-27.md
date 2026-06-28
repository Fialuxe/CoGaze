# CoGaze 動作フロー・カバレッジ最終監査（2026-06-27）

> 16エージェントのworkflow（Map→Audit×7→Verify×7→Synthesize）。各 audit を adversarial-verify で検証した **VERIFIED ステータス**に基づく。「定義済み≠実行時到達」を区別。raw出力: tasks/wf1dieloq.output。

---

## 1. Quest スリープ／取り外し（doff）／OS サスペンド時のフロー ←最重要

### 結論：一切ハンドルされていない（未対応・critical）
アプリのライフサイクルフックは**完全に不在**。`Assets/Scripts` 全体の grep で `OnApplicationPause` / `OnApplicationFocus` / `OnApplicationQuit` = 0 件、`Screen.sleepTimeout` / `NeverSleep` / `OVRManager.HMDMounted/HMDUnmounted` / `InputFocusAcquired/Lost` / `TrackingLost` = 0 件。スリープ・doff・近接センサ離脱・OS サスペンドに**反応するコードは存在しない**。

### 実際に起きること
- **(a) Photon ピアタイムアウト（~10s）未満の短い覗き見/doff**：接続維持、何も起きない（標準挙動からの推論・実機未観測）。
- **(b) タイムアウト超の doff/スリープ/OS サスペンド**：OS がアプリ凍結、Photon `KeepAliveInBackground`（60s）も完全サスペンド下で凍結。`runInBackground=1`（ProjectSettings.asset:86）は Android では無効。よって**切断は不可避**（実機確認推奨）。

### 復帰経路（静的に確定）
`NetworkManager.OnDisconnected`(:68) → `ReconnectAfterDelay(3f)`(:78-79) → 再接続 → `OnRoomJoined`(SceneBootstrapper2.cs:140) → `SetupAfterDeviceCheck`(:268) → Worker prefab を**新規** Instantiate(:299) → `SetupWorker`(:331) → `expMgr.Initialize(false)`(:370) → `CurrentState=Setup` を**無条件設定**(ExperimentManager2.cs:122)。新規 WorkerHUD2 が `SetState("セットアップ中...")`(WorkerHUD2.cs:519)。= **無音の Setup ロールバック**。

### 緩和策（唯一・偶発的）
Expert が **TaskRunning / WhiteNoise** のときに限り `PeriodicResync`(ExperimentManager2.cs:852-865, 10sループ)が再ブロードキャストし Worker は ≤10s で復帰（ただし先に「セットアップ中」表示・再同期表示なし）。**ゲート状態（Idle/Ready/TaskComplete/NoiseComplete/Questionnaire/Finished）では再ブロードキャストなし**：PeriodicResync は active 状態限定、`BroadcastCurrentState`(:825) は呼出元ゼロ、pull 経路 `DelaySyncRequest` は guard(:841)で初回 break。→ **Worker はオペレータが手動で1段進めるまで「セットアップ中」固着**、双方待ちでデッドロック。

### 付随する悪化
- **Expert(PC)スリープ**で `Initialize(true)`(SB2:587)再実行→`StartExperiment`が `CurrentStepIndex=0`(:321)→**条件1から全やり直し**。gaze-key-lock も無効化。
- **長尺ステップの自己スリープ**：`Screen.sleepTimeout=NeverSleep` 不在で Assembly(180s)中に Quest が自前スリープし得る。タイマーは頭から外しても**条件を進め続ける**。
- **両者サスペンド**：`EmptyRoomTtl=0`(NetworkManager.cs:47-53)で room 即破棄→フル再セットアップ。

**要実機確認**：doff が timeout 超で切断する点・フォーカス喪失で Enter/Del 不達は標準挙動からの推論。「フック皆無」は静的に確定。

---

## 2. 重大ギャップ ランキングと最小修正

**共通根因**：critical 2件＋high数件は2つの根に収束 ―（i）`Initialize` が冪等性ガード無しに `CurrentState=Setup` を無条件設定(ExperimentManager2.cs:122)、（ii）run cursor 永続化が皆無。**「Initialize 冪等化＋step index 永続/復元」の1対の修正**で複数の上位ギャップが同時解消、gaze-key-lock 順序問題も自動的に解ける。

**Critical**
1. **Expert 再接続/再起動で進捗全損（+gaze-key-lock 無効化）** — Initialize 冪等化＋CurrentStepIndex/conditionOrder を永続化し復元。
2. **Quest doff/サスペンドで無音 Setup ロールバック（ゲート状態で再同期不能）** — Worker に `OnApplicationPause(false)` で state 再要求；`BroadcastCurrentState`(:825)を Expert ボタン/キーに配線、または全状態 heartbeat 化。

**High**
3. ゲート状態 Worker 再接続デッドロック — BroadcastCurrentState 配線＋DelaySyncRequest の Idle ガード緩和。
4. 両者切断で room 破棄 — RoomOptions に EmptyRoomTtl/PlayerTtl 設定。
5. task 中 mesh-calib grip による無音の条件汚染 — UpdateCalibration を Setup state 限定（※要実機: IsMine 所有）。
6. 誤 calib 復旧トリガが dead — `RequestResetDualCalibration` を Expert キー/ボタンに配線。
7. コントローラ電池/接続監視なし — インジケータ＋入力喪失プロンプト。
8. MRUK tracker 失敗の区別なし — 起動不能フラグを UI 伝播＋self-check。

**Medium**：RECON-06 A/V 非対称（Expert単独dropでWorker映像凍結）／再Initialize event二重購読／uncalibrated未記録（trials.csvにcalib_quality列）／calibration/abort未送出／late-result race／decode-fail cue欠／Expert mismatch表示欠／Del-skipアンケートmissing未記録／重複participantガード無／graceful quit（OnApplicationQuitでflush/finalize/EndSession）。

**Low**：AudioSourceリーク／timeout 30s案内不足／session_start NACK無通知／Worker側presence欠／ESCAL F4非gate／osc_certainty dead列／EndTrial stale marker／trialVoiceStartSeconds no-op。

---

## 3. 今セッションの新規コード判定

**(A) アンケート 17/14＋per-item hints＋gaze_items ＝ 正しい**
`BuildConditionPanel`(QuestionnaireManager.cs:303-318)で gaze=17 / NoGaze=14。NoGaze判定 static `Conditions[idx].gaze==None`。per-item ScaleHint で NASA=高い悪／gaze=高い良 切替。integrity gate(:851-868)で id6+as6 未達は保存中断。NoGaze欠測は score=-1/missing=true で矩形維持。JsonUtility安全・atomic書込。

**(B) escalation F2/F3/F4 ＝ 概ね正しいが1バグ**
Expert限定(AddComponentはSetupExpertのみ:662、Updateはtrial非activeでreturn)○。trials.csv 16列整合○。escalations.csv append+AutoFlush+close○。
- **バグ ESCAL-01**: F4（rung4=向き、本来Assembly限定）が StepType.Assembly に gate されず(:329-331)、60s識別trial中の誤F4がrung4として混入。**データ妥当性のみ・実行時故障なし(low)**。

**(C) timers 60/180/20 ＝ 正しい**
`ExperimentScene.unity:1681-1684`で task=60/assembly=180/whiteNoise=20。whiteNoiseVolume=0.4 が script既定0.30f と異なる＝**scene値が実行時権威**の証左。

**加えて判明した具体バグ（新規コード）**
- **osc_certainty 列が常に -1.0000**: `SetOscCertainty`(ExperimentLogger.cs:99)呼出元ゼロ。dead telemetry。
- **EndTrial が Assembly 行に旧識別の identified_marker を記録**: CompletedMarkerId が EndTask で未クリア→Assembly行に stale値。correct空欄なので step_type でフィルタ可。low。
- **trialVoiceStartSeconds 二重代入 no-op**: 常に0、無害（legacy残骸）。
- **Del-hold force-advance がアンケートを欠測記録せず破棄**（RPC無でWorker panel未解除）。

---

## 4. 対応済みで安全（確認済み）
識別60s/Assembly180s（早期完了非対称は意図的）／識別完了gate bypass＋wrong-QR採点／NASA submit auto-advance／Expert早押しEnter遮断／Finished+SSQ submitゲート／WhiteNoise20s呼吸ガイド／3条件毎Rest／dual-QR完了(HUD+両手haptic+承認)／IR/NoGazeはwebcam calib不要／Python handshake 30s timeout／入力バインディング（識別=GRIP/calib=TRIGGER+X/手動QR=GRIP）／late-join Expertのcalib-complete復元(AllBuffered)／ConnectIdentificationTask・ConnectMeshHandler配線（旧deadが復活、SB2:381,385）。

## 5. 要実機確認（静的に確定できず）
doff切断閾値・フォーカス喪失でのキー不達／INPUT-01のIsMine所有（Expert=MasterならWorker手動calib死亡の可能性）／再接続時prefab重複アバター（PUN CleanupCacheOnLeave依存）／MARGINAL×2が30s超でspurious timeout／dual-QR indicatorA/B SerializeField割当（scene設定）／participantOrderIndex非同期（両端末StartupConfig一致前提）／haptics/grip ブロックのQuest実行。
