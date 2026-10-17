using ZemiLog.Core;

namespace ZemiLog.App;

public sealed record AudioChunk(byte[] Pcm16, TimeSpan Start, TimeSpan End, SpeakerRole Speaker);

public sealed record MicrophoneChoice(string Id, string Name)
{
    // 動画の要約など、自分が話さないときは相手の音だけを録る
    public static MicrophoneChoice None { get; } = new(string.Empty, "使わない（相手の音だけ録る）");
    public bool IsNone => Id.Length == 0;
    // スピーカー出力をそのまま録る入力デバイス。マイク欄で選ぶと「PC全体の音」と同じ音が自分の発言として二重に入る
    private static readonly string[] MixerNames = ["ステレオ ミキサー", "ステレオミキサー", "Stereo Mix", "What U Hear", "Wave Out Mix"];
    public bool IsMixer => MixerNames.Any(x => Name.Contains(x, StringComparison.OrdinalIgnoreCase));
    public override string ToString() => Name;
}

public sealed record ProcessChoice(int Id, string Name)
{
    // アプリを選ばず、既定のスピーカーに流れている音をまるごと録る（ステレオミキサー相当）
    public const int WholeSystemId = -1;
    public static ProcessChoice WholeSystem { get; } = new(WholeSystemId, "PC全体の音（既定のスピーカー出力）");
    public bool IsWholeSystem => Id == WholeSystemId;
    public override string ToString() => IsWholeSystem ? Name : $"{Name} ({Id})";
}
