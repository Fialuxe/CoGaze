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
    public const string Expert2_BottomHint =
        "[Tab] 左パネル切替  ／  [Del] スキップ  ／  [Enter] 確定";

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
    public const string Worker_TaskComplete  = "タスク終了\n次へ進んでください";
    public const string Worker_NoiseComplete = "インターバル終了\n次へ進んでください";
    public const string Worker_Questionnaire = "アンケート記入中";
    public const string Worker_Finished      = "実験終了\nありがとうございました";

    // ── Step / progress labels (HandleProgressChanged) ───────────────────
    public const string Worker_NoiseInProgress      = "インターバル中... 次のタスクをお待ちください";
    public const string Worker_TaskNoGaze           = "識別課題:\nQRマーカーを自分で判断して指し示してください";
    public const string Worker_TaskWithGaze         = "識別課題:\nExpertの視線が示すQRマーカーを指し示してください";
    public const string Worker_AssemblyNoGaze       = "組み立て課題:\n自分でSomaキューブを正しい位置に置いてください";
    public const string Worker_AssemblyWithGaze     = "組み立て課題:\nExpertの視線が示す位置にSomaキューブを置いてください";
    public const string Worker_QuestionnaireStep    = "アンケートに回答してください";
    public const string Worker_ConditionNextLabel   = "次の条件";
    public const string Worker_ConditionStartSuffix = " を開始します..";

    // ── QR identification task ────────────────────────────────────────────
    public const string Worker_QRFound    = "QRコード確認済\nコントローラーをQRに近づけてグリップを押してください";
    public const string Worker_QRSearching = "識別課題: QRマーカーを探して正面を向いてください";

    // ── Breathing guide ───────────────────────────────────────────────────
    public const string Worker_BreathIn            = "ゆっくり 吸って";
    public const string Worker_BreathOut           = "ゆっくり 吐いて";
    public const string Worker_BreathIntervalLabel = "インターバル";

    // ── Alert marker ─────────────────────────────────────────────────────
    public const string Worker_AlertExclamation = "!";

    // ═══════════════════════════════════════════════════════════════════════
    //  Calibration (Worker)
    // ═══════════════════════════════════════════════════════════════════════
    // ── Manual calibration hints (hold-X mode) ───────────────────────────
    public const string Calib_MoveXZ      = "グリップで掴んでメッシュを移動";
    public const string Calib_AdjustHeight = "手を動かすと位置が変わります";
    public const string Calib_Rotate       = "右スティック → 回転";
    public const string Calib_Confirm      = "X を離すと位置を確定・送信";
    public const string Calib_FullHint     = "X長押し中｜グリップ=掴んで移動  /  スティック=回転  /  離す=確定";
    public const string Calib_Sent         = "✓ 送信完了";

    // ── Dual-QR automatic calibration steps ──────────────────────────────
    public const string DualCalib_NeedsA   = "キャリブレーション: QR-A をスキャンしてください";
    public const string DualCalib_NeedsB   = "✓ QR-A スキャン済  →  QR-B をスキャンしてください";
    public const string DualCalib_Complete  = "✓ キャリブレーション完了！ Expert に送信しました";
}
