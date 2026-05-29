namespace Speak2Docs

module Text =
    type ProcessedQuery = FsVoice.Retrieval.ProcessedQuery

    let notEmpty value = FsVoice.Core.Text.notEmpty value
    let splitLines value = FsVoice.Core.Text.splitLines value

    let normalizeWhitespace value =
        FsVoice.Core.Text.normalizeWhitespace value

    let stripHtml value = FsVoice.Core.Text.stripHtml value
    let terms value = FsVoice.Core.Text.terms value

    let truncate maxChars value =
        FsVoice.Core.Text.truncate maxChars value

    module QueryPostProcessing =
        let forVoiceLikeRetrieval query =
            FsVoice.Retrieval.QueryPostProcessing.forVoiceLikeRetrieval query

        let forVoiceLikeRetrievalWithProfile profile query =
            FsVoice.Retrieval.QueryPostProcessing.forVoiceLikeRetrievalWithProfile profile query

module ModelCapabilities =
    let supportsTemperature modelId =
        FsVoice.Ctx.ModelCapabilities.supportsTemperature modelId
