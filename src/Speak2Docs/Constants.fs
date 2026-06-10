namespace Speak2Docs

module C =
    let PRODUCT_NAME = "Speak2Docs"
    let APP_NAME = PRODUCT_NAME
    let TOOL_PLUGIN_NAME = "FsVoiceTools"
    let TERMS_VERSION = "2026-06-06"
    let OPENAI_DATA_DISCLOSURE_VERSION = "2026-06-06.2"
    let TERMS_URL = "https://fwaris.github.io/docs/fsvoice/terms.html"
    let PRIVACY_POLICY_URL = "https://fwaris.github.io/docs/fsvoice/privacy.html"

    let THIRD_PARTY_NOTICES_URL =
        "https://fwaris.github.io/docs/fsvoice/third-party-notices.html"

    let SETTINGS_HELP_URL = "https://fwaris.github.io/docs/fsvoice/settings.html"

    let SETTINGS_OPENAI_KEY = "FsVoice.OpenAIKey"
    let SETTINGS_PDF_LIBRARY = "FsVoice.PdfLibrary"
    let SETTINGS_RETRIEVAL_MODE = "FsVoice.RetrievalMode"
    let SETTINGS_LOG_EXPANSIONS = "FsVoice.LogExpansions"
    let SETTINGS_LOG_CHUNKS = "FsVoice.LogChunks"
    let SETTINGS_ACTIVITY_LOG_LEVEL = "FsVoice.ActivityLogLevel"
    let SETTINGS_AUDIO_DEFAULT_TO_SPEAKER = "FsVoice.AudioDefaultToSpeaker"

    let SETTINGS_IOS_RECEIVER_AUDIO_ROUTE_MIGRATED =
        "FsVoice.IosReceiverAudioRouteMigrated"

    let SETTINGS_ANSWER_MAX_OUTPUT_TOKENS = "FsVoice.AnswerMaxOutputTokens"
    let SETTINGS_ANSWER_REASONING_EFFORT = "FsVoice.AnswerReasoningEffort"
    let SETTINGS_ANSWER_TOOL_CALL_LOOP_LIMIT = "FsVoice.AnswerToolCallLoopLimit"
    let SETTINGS_USE_LEXICAL_FILTER = "FsVoice.UseLexicalFilter"
    let SETTINGS_ELABORATE_INDEX_KEYWORDS = "FsVoice.ElaborateIndexKeywords"
    let SETTINGS_USE_HYBRID_PDF_PARSING = "FsVoice.UseHybridPdfParsing"
    let SETTINGS_USE_LAYOUT_ANALYSIS = "FsVoice.UseLayoutAnalysis"
    let SETTINGS_DESCRIBE_PDF_VISUALS = "FsVoice.DescribePdfVisuals"
    let SETTINGS_ACTIVE_PLUG_IN = "FsVoice.ActivePlugIn"
    let SETTINGS_HIDDEN_BUILT_IN_SOURCES = "FsVoice.HiddenBuiltInSources"
    let SETTINGS_ACCEPTED_TERMS_VERSION = "FsVoice.AcceptedTermsVersion"

    let SETTINGS_SUPPRESS_OPENAI_DATA_DISCLOSURE_VERSION =
        "FsVoice.SuppressOpenAIDataDisclosureVersion"

    let DEFAULT_ORACLE_MODEL = "gpt-5.5"
    let DEFAULT_REALTIME_MODEL = "gpt-realtime-2"
    let DEFAULT_VISUAL_DESCRIPTION_MODEL = "gpt-5-mini"
    let DEFAULT_ANSWER_MAX_OUTPUT_TOKENS = 2500
    let MIN_ANSWER_MAX_OUTPUT_TOKENS = 128
    let MAX_ANSWER_MAX_OUTPUT_TOKENS = 32000
    let DEFAULT_ANSWER_REASONING_EFFORT = "low"
    let DEFAULT_ANSWER_TOOL_CALL_LOOP_LIMIT = 3
    let MIN_ANSWER_TOOL_CALL_LOOP_LIMIT = 1
    let MAX_ANSWER_TOOL_CALL_LOOP_LIMIT = 8
    let NANO_MODEL = "gpt-5-nano"
    let REALTIME_MEMORY_TIMEOUT_MS = 1200
    let REALTIME_MEMORY_CANDIDATE_CHUNKS = 14
    let REALTIME_MEMORY_MAX_CONTEXT_CHUNKS = 12
    let REALTIME_MEMORY_NEIGHBOR_SEEDS = 4
    let MAX_LOG = 250
    let FONT_REG = "OpenSansRegular"
    let FONT_BOLD = "OpenSansSemibold"
    let FONT_SYMBOLS = "MaterialSymbols"
