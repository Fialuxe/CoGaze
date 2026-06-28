/// <summary>
/// All user-facing UI display strings for CoGaze, grouped by owner.
///
/// Previously split across five partial files (CoGazeStrings_Worker / _Expert2 /
/// _Startup / _Experiment / _Debug). Consolidated into this single file so a string
/// can be found in one place instead of guessing which partial holds it. Keep the
/// `partial` keyword so any future per-feature split is still possible without churn.
/// </summary>
public static partial class CoGazeStrings
{
    // ═══════════════════════════════════════════════════════════════════════
    //  StartupUI
    // ═══════════════════════════════════════════════════════════════════════
    public const string Startup_Title               = "CoGaze 実験設定";
    public const string Startup_LabelParticipantId  = "参加者 ID";
    public const string Startup_LabelPythonHost     = "Python ホスト IP";
    public const string Startup_HintPythonHost      = "同一 PC → 127.0.0.1  /  別 PC → 192.168.x.x";
    public const string Startup_LabelMicrophone     = "マイク";
    public const string Startup_ToggleOfflineMode   = "  オフラインモード（Photon 不使用・動作確認用）";
    public const string Startup_ButtonStart         = "開始";

    // ═══════════════════════════════════════════════════════════════════════
    //  ExperimentManager2 — fallback instructions
    // ═══════════════════════════════════════════════════════════════════════
    public const string Exp_InstructionsMissing = "⚠ instructions_new.txt が見つかりません。StreamingAssets に配置してください。";

    // ═══════════════════════════════════════════════════════════════════════
    //  DebugHUD
    // ═══════════════════════════════════════════════════════════════════════
    public const string Debug_Title = "─── CoGaze Debug ───";

    // ═══════════════════════════════════════════════════════════════════════
    //  ExpertUI2
    // ═══════════════════════════════════════════════════════════════════════
    // ── Top-bar initial values ────────────────────────────────────────────
    public const string Expert2_HeaderDefault    = "ステップ -/-";
    public const string Expert2_StateDefault     = "待機中";
    public const string Expert2_TimerBlank       = "--:--";
    public const string Expert2_TimerZero        = "00:00";
    public const string Expert2_PythonDefault    = "Python: --";

    // ── Python status labels ──────────────────────────────────────────────
    public const string Expert2_PythonNG         = "Python: NG";
    public const string Expert2_PythonWaiting    = "Python: ...";

    // ── Bottom bar hint ───────────────────────────────────────────────────
    // Kept as the generic fallback / initial value. ExpertUI2 swaps to the state-contextual
    // variants below so R / M / Setup-approval actions become discoverable (UX13).
    public const string Expert2_BottomHint =
        "[Tab] 左パネル切替  ／  [Del]長押し スキップ  ／  [Enter] 確定";

    // ── Bottom bar hint — state-contextual variants (UX13) ────────────────
    // Surface the actions that are otherwise undiscoverable: [M] remote-mesh toggle,
    // [R] calibration retry, and the Setup approval button.
    public const string Expert2_HintSetup    = "Worker 準備完了後、承認ボタンで開始  ／  [M] メッシュ表示";
    public const string Expert2_HintReady    = "[Enter] 実験開始  ／  [M] メッシュ表示  ／  [Tab] パネル切替";
    public const string Expert2_HintTask     = "[Del]長押し スキップ  ／  [Tab] 左パネル切替";
    public const string Expert2_HintCalibGate = "[Enter] 開始  ／  [R] キャリブ再試行  ／  [M] メッシュ表示";
    public const string Expert2_HintGate     = "[Enter] 次へ  ／  [Tab] 左パネル切替";
    public const string Expert2_HintRest     = "[Enter] 再開  ／  [Tab] 左パネル切替";

    // ── Rest break (Expert top-bar / panel; UX11) ─────────────────────────
    // Rest reuses the Questionnaire gate state, but the Expert label must read 休憩中, not アンケート中.
    public const string Expert2_RestState = "休憩中";
    public const string Expert2_RestHint  = "[Enter] 再開";

    // ── Task complete after the identification task (UX12) ────────────────
    // The identification task is NOT followed by a questionnaire (only Assembly is), so the
    // operator must not be told to run one that does not exist.
    public const string Expert2_TaskCompleteDetail_Identify =
        "識別課題が完了しました。\nこの課題にアンケートはありません。\n\n[Enter] で次へ進んでください。";

    // ═══════════════════════════════════════════════════════════════════════
    //  WorkerHUD2
    // ═══════════════════════════════════════════════════════════════════════
    // ── Connection status ─────────────────────────────────────────────────
    public const string Worker_ConnChecking      = "● 接続確認中...";
    public const string Worker_ConnDisconnected  = "[!] 切断中...";
    public const string Worker_ConnExpertOnline  = "● Expert 接続済";
    public const string Worker_ConnExpertWaiting = "○ Expert 待機中";

    // ── Timer placeholders ────────────────────────────────────────────────
    public const string Worker_TimerEmpty           = "--:--";
    public const string Worker_TimerZero            = "00:00";
    public const string Worker_BreathCountdownEmpty = "あと --:--";

    // ── State labels (HandleStateChanged) ────────────────────────────────
    public const string Worker_Idle          = "準備中...";
    public const string Worker_StateIdle     = "待機中...";
    public const string Worker_Ready         = "開始を待っています";
    public const string Worker_TaskRunning   = "タスク実行中";
    // UX5: the subject has no progression control here, so the verb is passive ("just wait"),
    // not an active "proceed" that would send them hunting for a button that does not exist.
    public const string Worker_TaskComplete  = "タスク終了\nそのままお待ちください";
    public const string Worker_NoiseComplete = "インターバル終了\nそのままお待ちください";
    public const string Worker_Questionnaire = "アンケート記入中";
    public const string Worker_Finished      = "実験終了\nありがとうございました";

    // ── Step / progress labels (HandleProgressChanged) ───────────────────
    public const string Worker_NoiseInProgress      = "インターバル中... 次のタスクをお待ちください";
    public const string Worker_TaskNoGaze           = "識別課題:\nExpertの口頭指示に従ってQRマーカーを指し示してください";
    public const string Worker_TaskWithGaze         = "識別課題:\nExpertの視線が示すQRマーカーを指し示してください";
    public const string Worker_AssemblyNoGaze       = "組み立て課題:\nExpertの口頭指示に従ってSomaキューブを置いてください\n（この条件ではExpertの視線は提示されません）";
    public const string Worker_AssemblyWithGaze     = "組み立て課題:\nExpertの視線が示す位置にSomaキューブを置いてください";
    public const string Worker_QuestionnaireStep    = "アンケートに回答してください";
    public const string Worker_ConditionNextLabel   = "次の条件";
    public const string Worker_ConditionStartSuffix = " を開始します..";

    // ── Identification task: live score ──────────────────────────────────
    // Shown on Worker HUD below the QR approach instruction; target ID is never shown to Worker.
    public static string Worker_ScoreFormat(int score) => $"正解数: {score}";

    // ── QR identification task ────────────────────────────────────────────
    // Answer = bring the controller near the target QR and press the GRIP (matches the briefing video).
    // Worker_QRFound is shown for the whole task now (IdentificationTask fires the "found" state at
    // task start), so it must be self-contained; Worker_QRSearching is no longer shown at runtime.
    public const string Worker_QRFound    = "識別課題: Expertが示す対象のQRにコントローラーを近づけて\nグリップボタンを押してください";
    public const string Worker_QRSearching = "識別課題: 対象のQRを探してください";
    // NoGaze (control) variant: no gaze indicator is shown, so direct by voice and tell the subject
    // explicitly that gaze is absent this condition (so a missing indicator doesn't read as a fault).
    public const string Worker_QRFoundNoGaze = "識別課題: 担当者の音声指示で示されたQRにコントローラーを近づけて\nグリップボタンを押してください\n（この条件ではExpertの視線は提示されません）";

    // ── Breathing guide ───────────────────────────────────────────────────
    public const string Worker_BreathIn            = "ゆっくり 吸って";
    public const string Worker_BreathOut           = "ゆっくり 吐いて";
    public const string Worker_BreathIntervalLabel = "インターバル";

    // ── Alert marker ─────────────────────────────────────────────────────
    public const string Worker_AlertExclamation = "!";

    // ── Rest break (auto-inserted every few conditions; operator resumes with Enter) ──────
    public const string Rest_Worker = "休憩中です\n準備ができたら担当者にお知らせください";
    public const string Rest_Expert = "休憩中 — 再開するには Enter を押してください";

    // ── Expert setup readiness, shown on the Worker during Setup ──────────
    public const string Worker_ExpertPreparing = "実験者: 準備中…";
    public const string Worker_ExpertReady     = "実験者: 準備完了";

    // ── Condition start (Worker HUD) ──────────────────────────────────────
    // Must NOT use MessageBank: Android skips file loading, so keys leak as raw English.
    public static string Exp_CondStartWorker(int pos, int total) =>
        $"【条件 {pos}/{total}】  次の条件を準備しています。しばらくお待ちください。";

    // ═══════════════════════════════════════════════════════════════════════
    //  Calibration (Worker)
    // ═══════════════════════════════════════════════════════════════════════
    // ── Manual calibration hints (hold-X mode) ───────────────────────────
    // UX17: drop the "メッシュ" jargon for the (non-expert) subject; describe the action plainly.
    public const string Calib_MoveXZ      = "インデックストリガー（人差し指）で掴んで移動";
    public const string Calib_AdjustHeight = "手を動かすと位置が変わります";
    public const string Calib_Rotate       = "右スティック → 回転";
    public const string Calib_Confirm      = "X を離すと位置を確定・送信";
    public const string Calib_FullHint     = "X長押し中｜インデックストリガー=掴んで移動  /  スティック=回転  /  離す=確定";
    public const string Calib_Sent         = "✓ 送信完了";

    // ── Dual-QR automatic calibration steps ──────────────────────────────
    public const string DualCalib_NeedsA   = "キャリブレーション: QR-A をスキャンしてください";
    public const string DualCalib_NeedsB   = "✓ QR-A スキャン済  →  QR-B をスキャンしてください";
    public const string DualCalib_Complete  = "✓ キャリブレーション完了！ Expert に送信しました";
}
