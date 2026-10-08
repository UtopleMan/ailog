namespace AiLog.Shared;

/// <summary>Text helpers shared by the adapters, classifiers and segment previews.</summary>
internal static class StringExtensions
{
    private const string Ellipsis = "…";

    extension(string text)
    {
        /// <summary>The text cut to at most <c>max</c> characters, with an ellipsis marking the cut.</summary>
        public string Shorten(int max) => text.Length <= max ? text : text[..max] + Ellipsis;
    }
}
