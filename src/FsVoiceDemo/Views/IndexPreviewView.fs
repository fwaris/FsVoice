namespace FsVoiceDemo.Views

open System
open System.Globalization
open Fabulous.Maui
open FsVoiceDemo
open FsVoiceDemo.WorkFlow
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module IndexPreviewView =
    let private compactWhitespace (value: string) =
        if String.IsNullOrWhiteSpace value then
            ""
        else
            value.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> String.concat " "

    let private truncateText maxLength value =
        let text = compactWhitespace value

        if text.Length <= maxLength then
            text
        else
            $"{text.Substring(0, maxLength)}..."

    let private listText values =
        match values |> List.truncate 16 with
        | [] -> "None"
        | items -> String.concat ", " items

    let private vectorText (vector: KnowledgeSources.IndexPreviewVectorSummary) =
        let values =
            vector.valueSample
            |> List.map (fun value -> value.ToString("0.0000", CultureInfo.InvariantCulture))
            |> String.concat ", "

        $"{vector.embeddingDim} x {vector.tokenCount} - Sample [{values}]"

    let private mutedLabel text =
        Label(text).font(size = 12.).textColor(Colors.LightGray).lineBreakMode (LineBreakMode.WordWrap)

    let private recordView (record: KnowledgeSources.IndexPreviewRecord) =
        Border(
            (VStack(spacing = 6.) {
                Label($"Chunk {record.index}")
                    .font(size = 13., attributes = FontAttributes.Bold)
                    .textColor(Colors.Magenta)
                    .lineBreakMode (LineBreakMode.TailTruncation)

                Label(truncateText 900 record.text).font(size = 12.).lineBreakMode (LineBreakMode.WordWrap)

                mutedLabel $"Keywords: {listText record.keywords}"
                mutedLabel $"Terms: {listText record.terms}"
                mutedLabel $"Vectors: {vectorText record.vector}"
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Colors.LightGray))
            .strokeThickness(1.)
            .strokeShape(RoundRectangle(CornerRadius(8.)))
            .margin (0., 0., 0., 8.)

    let private header isLoading =
        Grid([ Dimension.Absolute 42.; Dimension.Star; Dimension.Absolute 42. ], [ Dimension.Absolute 44. ]) {
            (ViewControls.compactIconButton Icons.back IndexPreviewBack).gridColumn(0).gridRow (0)

            Label("Index Preview")
                .font(size = 16., attributes = FontAttributes.Bold)
                .centerVertical()
                .gridColumn(1)
                .gridRow (0)

            (ViewControls.compactIconButton Icons.refresh RefreshIndexPreview)
                .isEnabled(not isLoading)
                .gridColumn(2)
                .gridRow (0)
        }

    let private messageView title detail =
        Border(
            (VStack(spacing = 8.) {
                Label(title)
                    .font(size = 14., attributes = FontAttributes.Bold)
                    .textColor(Colors.DimGray)
                    .centerHorizontal ()

                if not (String.IsNullOrWhiteSpace detail) then
                    Label(detail)
                        .font(size = 12.)
                        .textColor(Colors.DimGray)
                        .lineBreakMode(LineBreakMode.WordWrap)
                        .centerHorizontal ()
            })
                .padding (16.)
        )
            .stroke(SolidColorBrush(Colors.LightGray))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private summaryView (preview: KnowledgeSources.IndexPreview) =
        Border(
            (Grid([ Dimension.Star ], [ Dimension.Absolute 26.; Dimension.Absolute 24. ]) {
                Label(preview.source.DisplayName)
                    .font(size = 13., attributes = FontAttributes.Bold)
                    .lineBreakMode(LineBreakMode.MiddleTruncation)
                    .centerVertical()
                    .gridRow (0)

                Label($"Showing {preview.sampledCount} random chunk(s) from {preview.totalChunks}.")
                    .font(size = 12.)
                    .textColor(Colors.DimGray)
                    .centerVertical()
                    .gridRow (1)
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Colors.LightGray))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private previewContent (preview: KnowledgeSources.IndexPreview) =
        Grid([ Dimension.Star ], [ Dimension.Absolute 102.; Dimension.Star ]) {
            (summaryView preview).gridRow (0)

            if List.isEmpty preview.records then
                (messageView "No preview records" "The index contains no sampled chunks.").gridRow (1)
            else
                (CollectionView (preview.records) recordView).gridRow (1)
        }

    let contentPage (model: Model) =
        let isLoading =
            match model.indexPreview with
            | Some(PreviewLoading _) -> true
            | Some(PreviewReady _)
            | Some(PreviewFailed _)
            | None -> false

        ContentPage(
            (Grid([ Dimension.Star ], [ Dimension.Absolute 52.; Dimension.Star ]) {
                (header isLoading).gridRow (0)

                match model.indexPreview with
                | Some(PreviewReady preview) -> (previewContent preview).gridRow (1)
                | Some(PreviewFailed(_, error)) -> (messageView "Preview unavailable" error).gridRow (1)
                | Some(PreviewLoading _)
                | None -> (messageView "Loading index preview..." "").gridRow (1)
            })
                .padding (18.)
        )
            .title ("Index Preview")
