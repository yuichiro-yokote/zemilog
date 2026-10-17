using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using ZemiLog.Core;

namespace ZemiLog.Tests;

public sealed class PresentationReaderTests
{
    [Fact]
    public void Read_PreservesSlideOrderAndText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ZemiLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "発表.pptx");
        try
        {
            using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
            {
                var presentationPart = document.AddPresentationPart();
                presentationPart.Presentation = new P.Presentation { SlideIdList = new P.SlideIdList() };
                AddSlide(presentationPart, 256, "研究目的");
                AddSlide(presentationPart, 257, "提案手法");
                presentationPart.Presentation.Save();
            }

            var context = new PresentationReader().Read(path);

            Assert.Equal("発表.pptx", context.SourceFileName);
            Assert.Collection(context.Slides,
                slide => { Assert.Equal(1, slide.Number); Assert.Equal("研究目的", slide.Title); },
                slide => { Assert.Equal(2, slide.Number); Assert.Equal("提案手法", slide.Title); });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AddSlide(PresentationPart presentationPart, uint id, string text)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(text))))))));
        slidePart.Slide.Save();
        presentationPart.Presentation!.SlideIdList!.Append(new P.SlideId
        {
            Id = id,
            RelationshipId = presentationPart.GetIdOfPart(slidePart)
        });
    }
}
