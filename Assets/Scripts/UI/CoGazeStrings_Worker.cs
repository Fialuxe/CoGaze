/// <summary>
/// UI display strings for WorkerHUD2.
/// </summary>
public static partial class CoGazeStrings
{
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

    // ── Manual calibration hints (grip-mode) ─────────────────────────────
    public const string Calib_MoveXZ      = "スティック → XZ 移動";
    public const string Calib_AdjustHeight = "トリガー + スティックY → 高さ調整";
    public const string Calib_Rotate       = "トリガー + スティックX → 回転";
    public const string Calib_Confirm      = "A ボタン → 位置を確定・送信";
    public const string Calib_FullHint     = "GRIP: スティック=移動  /  トリガー+スティック=高さ・回転  /  A=確定";
    public const string Calib_Sent         = "✓ 送信完了";

    // ── Dual-QR automatic calibration steps ──────────────────────────────
    public const string DualCalib_NeedsA   = "キャリブレーション: QR-A をスキャンしてください";
    public const string DualCalib_NeedsB   = "✓ QR-A スキャン済  →  QR-B をスキャンしてください";
    public const string DualCalib_Complete  = "✓ キャリブレーション完了！ Expert に送信しました";
}
