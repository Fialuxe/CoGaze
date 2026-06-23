/// <summary>
/// ExpertUI (legacy) で使用される UI 表示文字列の定数定義。
/// </summary>
public static partial class CoGazeStrings
{
    // ── ExpertUI (legacy) ─────────────────────────────────────────────────

    // 初期値（BuildCanvas）
    public const string Expert_HeaderDefault      = "ステップ  -/-";
    public const string Expert_TimerDefault       = "--:--";
    public const string Expert_TimerZero          = "00:00";
    public const string Expert_LoadingInstruction = "instructions.txt を読み込んでいます...";

    // 状態バッジ
    public const string Expert_StateIdle          = "待機中";
    public const string Expert_StateReady         = "準備完了";
    public const string Expert_StateWhiteNoise    = "■ ノイズ再生中";
    public const string Expert_StateTaskRunning   = "タスク実行中です";
    public const string Expert_StateQuestionnaire = "アンケートに回答してください";
    public const string Expert_StateTaskComplete  = "タスク終了";
    public const string Expert_StateNoiseComplete = "ノイズ終了";
    public const string Expert_StateFinished      = "終了";

    // 指示テキスト（instructionText）
    public const string Expert_InstructionIdle          = "参加者の接続を待っています...";
    public const string Expert_InstructionReady         = "メッシュのキャリブレーションが終わっているかを確認し、[Enter] を押して実験を開始してください";
    public const string Expert_InstructionWhiteNoise    = "ホワイトノイズ再生中...";
    public const string Expert_InstructionTaskComplete  = "タスクが終了しました。アンケートへ回答し、回答が完了したら [Enter] を押してください。";
    public const string Expert_InstructionNoiseComplete = "ホワイトノイズが終了しました。次のステップへ進む場合は [Enter] を押してください。";
    public const string Expert_InstructionFinished      = "実験終了。ご協力ありがとうございました。";

    // ヒントテキスト（hintText）
    public const string Expert_HintStart = "[Enter] 開始";
    public const string Expert_HintSkip  = "[Del] スキップ";
    public const string Expert_HintDone  = "[Enter] 完了";
    public const string Expert_HintNext  = "[Enter] 次へ";
}
