namespace Anitools.Core.Media;

/// <summary>Дорожка субтитров из mkvmerge -J: ID дорожки, имя (тайтл или запасное), codec_id, языки.</summary>
/// <param name="Language">ISO 639-2 («rus»), пусто — не указан.</param>
/// <param name="LanguageIetf">BCP 47 («ru», «en-US»), пусто — не указан.</param>
public sealed record SubtitleTrack(int Id, string Name, string CodecId = "", string Language = "", string LanguageIetf = "");
