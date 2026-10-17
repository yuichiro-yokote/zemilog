using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace ZemiLog.Core;

public sealed class PresentationReader
{
    public PresentationContext Read(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentationPart = document.PresentationPart
            ?? throw new InvalidDataException("PowerPointのプレゼンテーション情報がありません。");
        var slideIdList = presentationPart.Presentation?.SlideIdList
            ?? throw new InvalidDataException("PowerPointにスライドがありません。");

        var slides = new List<PresentationSlide>();
        var number = 1;
        foreach (var slideId in slideIdList.Elements<SlideId>())
        {
            if (slideId.RelationshipId?.Value is not { } relationshipId) continue;
            var slidePart = (SlidePart)presentationPart.GetPartById(relationshipId);
            var paragraphs = (slidePart.Slide?.Descendants<A.Paragraph>() ?? [])
                .Select(ParagraphText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            var title = paragraphs.FirstOrDefault() ?? $"スライド {number}";
            var notes = (slidePart.NotesSlidePart?.NotesSlide?
                .Descendants<A.Paragraph>() ?? [])
                .Select(ParagraphText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Where(text => !int.TryParse(text, out _))
                .ToArray();
            slides.Add(new PresentationSlide(number, title, string.Join("\n", paragraphs), string.Join("\n", notes)));
            number++;
        }

        return new PresentationContext(Path.GetFileName(path), slides);
    }

    private static string ParagraphText(A.Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<A.Text>().Select(text => text.Text)).Trim();
}
