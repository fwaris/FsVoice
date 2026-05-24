namespace FsResponses

open System
open System.Net.Http.Headers
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.Extensions.AI
open System.Net.Http

type ResponseError =
    { code: string
      message: string
      ``type``: string option
      param: string option }

type ResponseErrorObj = { error: ResponseError }

type IncompleteDetails = { reason: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type Reasoning =
    { effort: string option
      summary: string option
      generate_summary: string option }

    static member Default =
        { effort = None
          summary = None
          generate_summary = None }

    static member Medium = "medium"
    static member High = "high"
    static member Low = "low"


type TextOutputFormat =
    | [<JsonName "text">] Text
    | [<JsonName "json_schema">] Json_schema of
        {| name: string
           schema: JsonElement
           strict: bool |}

type TextOutput = { format: TextOutputFormat }

type User_Location =
    { ``type``: string
      city: string option
      country: string option
      region: string option
      timezone: string option }

module SearchSizeContextSize =
    let low = "low"
    let medium = "medium"
    let high = "high"

module ComputerEnvironment =
    let browser = "browser"
    let mac = "mac"
    let windows = "windows"
    let ubuntu = "ubuntu"

module Truncation =
    let auto = "auto"
    let disabled = "disabled"

module Models =
    let gpt_41 = "gpt-4.1"
    let o4_mini = "o4-mini"
    let gpt_5 = "gpt-5"
    let gpt_5_mini = "gpt-5-mini"
    let gpt_41_nano = "gpt-4.1-nano"
    let gpt_41_mini = "gpt-4.1-mini"
    let computer_use_preview = "computer-use-preview"

module Buttons =
    [<Literal>]
    let Left = "left"

    [<Literal>]
    let Right = "right"

    [<Literal>]
    let Middle = "middle"

(*
type Property =
    {
        ``type``: string
        description: string
        properties: Map<string, Property>
        required: string list 
        items: Property
        enum: string list
        additionalProperties: bool
    }
*)

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type JsDesc = { description: string option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type JsString =
    { description: string option
      enum: string list option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type JsObj =
    { description: string option
      properties: Map<string, JsProperty>
      required: string list
      additionalProperties: bool }

and [<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>] JsArray =
    { description: string option
      items: JsProperty }

and [<RequireQualifiedAccess>] JsProperty =
    | [<JsonName "integer">] Integer of JsDesc
    | [<JsonName "number">] Number of JsDesc
    | [<JsonName "string">] String of JsString
    | [<JsonName "boolean">] Boolean of JsDesc
    | [<JsonName "array">] Array of JsArray
    | [<JsonName "object">] Object of JsObj

type Parameters =
    { ``type``: string
      properties: Map<string, JsProperty>
      required: string list
      additionalProperties: bool }

    static member Default =
        { ``type`` = "object"
          properties = Map.empty
          required = []
          additionalProperties = false }

type Function =
    { name: string
      description: string
      parameters: Parameters
      strict: bool }

    static member Default =
        { name = ""
          description = ""
          parameters = Parameters.Default
          strict = true }

[<RequireQualifiedAccess>]
type Tool =
    | [<JsonName "file_search">] File_search of
        {| vector_store_ids: string list
           filters: JsonElement option
           maximum_num_results: int option
           ranking_options: JsonElement option |}
    | [<JsonName "function">] Function of Function
    | [<JsonName "web_search">] Web_search_current of
        {| search_context_size: string
           user_location: User_Location option |}
    | [<JsonName "web_search_2025_08_26">] Web_search_2025_08_26 of
        {| search_context_size: string
           user_location: User_Location option |}
    | [<JsonName "web_search_preview">] Web_search of
        {| search_context_size: string
           user_location: User_Location option |}
    | [<JsonName "computer_use_preview">] Computer_use of
        {| display_height: int
           display_width: int
           environment: string |}

    static member DefaultWebSearch =
        Tool.Web_search_current
            {| search_context_size = SearchSizeContextSize.medium
               user_location = None |}

type OutputText =
    { text: string
      annotations: JsonElement option }

[<RequireQualifiedAccess>]
type Content =
    | [<JsonName "output_text">] Output_text of OutputText // {|text : string; annotations : JsonElement option|}
    | [<JsonName "input_text">] Input_text of {| text: string |}
    | [<JsonName "refusal">] Refusal of {| refusal: string |}
    | [<JsonName "input_image">] Input_image of {| image_url: string |}

type Message =
    { id: string option
      status: string option
      role: string
      content: Content list }

    static member Default =
        { id = None
          status = None
          role = "user"
          content = [] }

    static member OfText text =
        { Message.Default with
            content = [ Content.Input_text {| text = text |} ] }

type SafetyCheck =
    { id: string
      code: string
      message: string }

type OutputDetail =
    | [<JsonPropertyName "input_image">] Computer_screenshot of {| image_url: string |}
    | [<JsonPropertyName "not_used">] DoNotUse of {| text: string |} //this is only to make this a multi-case union so that serializaton adds the type tag

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ComputerCallOutput =
    { call_id: string
      acknowledged_safety_checks: SafetyCheck list
      output: OutputDetail
      current_url: string option }

type ReasoningSummary = { text: string; ``type``: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type ReasoningOutput =
    { id: string
      summary: ReasoningSummary list
      status: string option }

type Point = { x: int; y: int }
type Path = { path: Point list }

type Action =
    | [<JsonName "click">] Click of {| button: string; x: int; y: int |}
    | [<JsonName "scroll">] Scroll of
        {| x: int
           y: int
           scroll_x: int
           scroll_y: int |}
    | [<JsonName "keypress">] Keypress of {| keys: string list |} //ctrl, alt, shift
    | [<JsonName "type">] Type of {| text: string |}
    | [<JsonName "wait">] Wait
    | [<JsonName "screenshot">] Screenshot
    | [<JsonName "double_click">] Double_click of {| x: int; y: int |}
    | [<JsonName "drag">] Drag of Path
    | [<JsonName "move">] Move of {| x: int; y: int |}

type ComputerCall =
    { id: string
      status: string
      action: Action
      call_id: string
      pending_safety_checks: SafetyCheck list }

type FunctionCall =
    { id: string
      call_id: string
      name: string
      arguments: string }

type FunctionCallOutput = { call_id: string; output: string }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type GenericOutputItem =
    { id: string option
      status: string option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type FileSearchCall =
    { id: string option
      status: string option
      queries: string list option
      results: JsonElement option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type CodeInterpreterCall =
    { id: string option
      status: string option
      code: string option
      results: JsonElement option }

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type McpCall =
    { id: string option
      status: string option
      server_label: string option
      name: string option
      arguments: string option
      output: string option
      error: string option }

[<RequireQualifiedAccess>]
type IOitem =
    | [<JsonName "message">] Message of Message
    | [<JsonName "image">] Image of
        {| image: string
           annotations: JsonElement option |}
    | [<JsonName "file">] File of
        {| file: string
           annotations: JsonElement option |}
    | [<JsonName "function_call">] Function_call of FunctionCall
    | [<JsonName "function_call_output">] Function_call_output of FunctionCallOutput
    | [<JsonName "web_search">] Web_search of
        {| search_context_size: string
           user_location: User_Location option |}
    | [<JsonName "web_search_call">] Web_search_call of GenericOutputItem
    | [<JsonName "file_search_call">] File_search_call of FileSearchCall
    | [<JsonName "code_interpreter_call">] Code_interpreter_call of CodeInterpreterCall
    | [<JsonName "mcp_call">] Mcp_call of McpCall
    | [<JsonName "mcp_approval_request">] Mcp_approval_request of McpCall
    | [<JsonName "mcp_approval_response">] Mcp_approval_response of McpCall
    | [<JsonName "local_shell_call">] Local_shell_call of GenericOutputItem
    | [<JsonName "image_generation_call">] Image_generation_call of GenericOutputItem
    | [<JsonName "computer_use_preview">] Computer_use of
        {| display_height: int
           display_width: int
           environment: string |}
    | [<JsonName "reasoning">] Reasoning of ReasoningOutput
    | [<JsonName "computer_call">] Computer_call of ComputerCall
    | [<JsonName "computer_call_output">] Computer_call_output of ComputerCallOutput

type Usage =
    { input_tokens: int
      output_tokens: int
      total_tokens: int
      input_tokens_details: JsonElement option
      output_tokens_details: JsonElement option }

[<RequireQualifiedAccess>]
[<JsonFSharpConverter(UnionUnwrapFieldlessTags = true)>]
type ToolChoice =
    | [<JsonName "none">] None
    | [<JsonName "auto">] Auto
    | [<JsonName "required">] Required

[<JsonFSharpConverter(SkippableOptionFields = SkippableOptionFields.Always)>]
type Request =
    { model: string
      input: IOitem list
      instructions: string option
      max_output_tokens: int option
      metadata: Map<string, string> option
      parallel_tool_calls: bool
      previous_response_id: string option
      reasoning: Reasoning option
      service_tier: string //auto, default, flex
      store: bool
      stream: bool
      temperature: float32
      text: TextOutput option
      tool_choice: ToolChoice
      tools: Tool list
      top_p: float32
      truncation: string option //auto, disabled
      user: string option }

    static member Default =
        { model = "gpt-4.1"
          input = []
          instructions = None
          max_output_tokens = None
          metadata = None
          parallel_tool_calls = false
          previous_response_id = None
          reasoning = None
          service_tier = "auto"
          store = false
          stream = false
          temperature = 1.0f
          text = None
          tool_choice = ToolChoice.Auto
          tools = []
          top_p = 1.0f
          truncation = Some Truncation.auto
          user = None }

type Response =
    { id: string
      ``object``: string option
      created_at: int64 option
      status: string
      error: ResponseError option
      incomplete_details: IncompleteDetails option
      instructions: string option
      max_output_tokens: int option
      model: string
      metadata: Map<string, string> option
      output: IOitem list
      parallel_tool_calls: bool option
      previous_response_id: string option
      reasoning: Reasoning option
      store: bool option
      temperature: float32
      text: TextOutput option
      tool_choice: JsonElement option
      tools: Tool list
      top_p: float32
      truncation: string option //auto, disabled
      usage: Usage option
      user: string option }

type List =
    { ``object``: string
      data: IOitem list
      first_id: string
      last_id: string
      has_more: bool }

//let testRespos = JsonSerializer.Deserialize<Response>(jsonObt, options=serOpts)
type DeleteResponse =
    { id: string
      object: string
      deleted: bool }

[<JsonFSharpConverter(UnionUnwrapFieldlessTags = true)>]
type SortOrder =
    | [<JsonName "asc">] Asc
    | [<JsonName "dsc">] Dsc

type ListRequest =
    { id: string
      before: string option
      after: string option
      limit: int option
      order: SortOrder option }

    static member Create id =
        { id = id
          before = None
          after = None
          limit = None
          order = None }

exception ApiError of ResponseErrorObj

///This indicates that a request was sent to the API without addressing the function calls from an earlier response
exception NoFuncCallOuput of ResponseErrorObj

module RUtils =
    open System.Text.Json.Schema

    let API_KEY_ENV_VAR = "OPENAI_API_KEY"

    let private shortenN (s: string) n =
        if s.Length < n then s else s.Substring(0, n) + "\u2026"

    let private shorten (s: string) = shortenN s 100

    let schema (t: Type) : JsonElement =
        let createOptions =
            AIJsonSchemaCreateOptions(
                TransformOptions = AIJsonSchemaTransformOptions(DisallowAdditionalProperties = true)
            )

        AIJsonUtilities.CreateJsonSchema(
            t,
            description = t.Name,
            serializerOptions = AIJsonUtilities.DefaultOptions,
            inferenceOptions = createOptions
        )


    ///Convert a type to JsonSchema and package it as `structured format`.
    ///Use a simple type structure for reliability
    let structuredFormat (t: Type) =
        let schema = schema t

        { format =
            Json_schema
                {| name = t.Name
                   schema = schema
                   strict = true |} }

    let parseContent<'t> (resp: Response) =
        resp.output
        |> List.tryPick (function
            | IOitem.Message m -> Some m
            | _ -> None)
        |> Option.bind (fun m ->
            m.content
            |> List.tryPick (function
                | Content.Refusal r -> Some(Choice2Of2 r.refusal)
                | Content.Output_text otxt ->
                    let t: 't = JsonSerializer.Deserialize<'t>(otxt.text)
                    Some(Choice1Of2 t)
                | _ -> None))

    let trimScreenshot (cco: OutputDetail) =
        match cco with
        | Computer_screenshot i -> Computer_screenshot {| image_url = shortenN i.image_url 20 |}
        | x -> x

    let trimImage =
        function
        | IOitem.Computer_call_output cco ->
            IOitem.Computer_call_output
                { cco with
                    output = trimScreenshot cco.output }
        | IOitem.Image i -> IOitem.Image {| i with image = shorten i.image |}
        | x -> x

    ///trim the large image base64 encoded string (to reduce log sizes)
    let trimResponse (resp: Response) =
        { resp with
            output = resp.output |> List.map trimImage }

    ///trim the large image base64 encoded string (to reduce log sizes)
    let trimRequest (req: Request) =
        { req with
            input = req.input |> List.map trimImage }

    let outputText (resp: Response) =
        [ for r in resp.output do
              match r with
              | IOitem.Message m ->
                  for c in m.content do
                      match c with
                      | Content.Output_text t -> yield t.text
                      | _ -> ()
              | _ -> () ]
        |> String.concat " "

    let toImageUri (bytes: byte[]) =
        let imageBytes = System.Convert.ToBase64String bytes
        $"data:image/png;base64,{imageBytes}"

module Api =

    let serOpts =
        let opts =
            JsonFSharpOptions
                .Default()
                //.WithSkippableOptionFields(true)
                .WithUnionInternalTag()
                .WithUnionTagName("type")
                .WithUnionUnwrapRecordCases()
                .WithUnionTagCaseInsensitive()
                .WithAllowNullFields()
                .WithAllowOverride()
                .WithUnionUnwrapFieldlessTags()
                .ToJsonSerializerOptions()

        opts.WriteIndented <- true
        opts

    let newClient (key: string) =
        let client = new HttpClient()
        client.BaseAddress <- Uri "https://api.openai.com/v1"
        client.DefaultRequestHeaders.Authorization <- new Headers.AuthenticationHeaderValue("Bearer", key)
        client

    let defaultClient () =
        newClient (Environment.GetEnvironmentVariable(RUtils.API_KEY_ENV_VAR))

    let create (req: Request) (client: #HttpClient) =
        task {
            let builder = UriBuilder(client.BaseAddress)
            builder.Path <- builder.Path + "/responses"
            let reqstr = JsonSerializer.Serialize(req, options = serOpts)

            if Log.debug_logging then
                Log.info $"Request: {reqstr} "
            //use! resp = client.PostAsJsonAsync(builder.Uri, req,options=serOpts)
            use strContent = new StringContent(reqstr, MediaTypeHeaderValue("application/json"))
            use! resp = client.PostAsync(builder.Uri, strContent)

            if
                resp.StatusCode = Net.HttpStatusCode.OK
                || resp.StatusCode = Net.HttpStatusCode.Accepted
            then
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"Response: {str} "

                return JsonSerializer.Deserialize<Response>(str, options = serOpts)
            else
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"{str} "

                let err =
                    try
                        let err = JsonSerializer.Deserialize<ResponseErrorObj>(str, options = serOpts)

                        if
                            err.error.message.Contains(
                                "No tool output found for function call",
                                StringComparison.CurrentCultureIgnoreCase
                            )
                        then
                            Some(NoFuncCallOuput err)
                        else
                            Some(ApiError err)
                    with ex ->
                        None

                match err with
                | Some e -> return raise e
                | None -> return failwith $"{str}"
        }

    let delete (id: string) (client: #HttpClient) =
        task {
            let builder = UriBuilder(client.BaseAddress)
            builder.Path <- builder.Path + $"/responses/{id}"
            let! resp = client.DeleteAsync(builder.Uri)

            if
                resp.StatusCode = Net.HttpStatusCode.OK
                || resp.StatusCode = Net.HttpStatusCode.Accepted
            then
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"Response Delete: {str} "

                return JsonSerializer.Deserialize<DeleteResponse>(str, options = serOpts)
            else
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"{str} "

                let err =
                    try
                        let err = JsonSerializer.Deserialize<ResponseErrorObj>(str, options = serOpts)
                        Some(ApiError err)
                    with ex ->
                        None

                match err with
                | Some e -> return raise e
                | None -> return failwith $"{str}"
        }

    let get (id: string) (client: #HttpClient) =
        task {
            let builder = UriBuilder(client.BaseAddress)
            builder.Path <- builder.Path + $"/responses/{id}"
            let! resp = client.GetAsync(builder.Uri)

            if
                resp.StatusCode = Net.HttpStatusCode.OK
                || resp.StatusCode = Net.HttpStatusCode.Accepted
            then
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"Response Get: {str} "

                return JsonSerializer.Deserialize<Response>(str, options = serOpts)
            else
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"{str} "

                let err =
                    try
                        let err = JsonSerializer.Deserialize<ResponseErrorObj>(str, options = serOpts)
                        Some(ApiError err)
                    with ex ->
                        None

                match err with
                | Some e -> return raise e
                | None -> return failwith $"{str}"
        }

    let list (req: ListRequest) (client: #HttpClient) =
        task {
            let builder = UriBuilder(client.BaseAddress)
            builder.Path <- builder.Path + $"/responses/{req.id}/input_items"

            let query =
                [ Option.map (fun r -> $"after={r}") req.after
                  Option.map (fun r -> $"before={r}") req.before
                  Option.map (fun r -> $"limit={r}") req.limit
                  Option.map
                      (fun r ->
                          $"""order={match r with
                                     | Asc -> "asc"
                                     | _ -> "dsc"}""")
                      req.order

                  ]
                |> List.choose id
                |> String.concat "&"

            builder.Query <- if String.IsNullOrWhiteSpace query then "" else "?" + query
            let! resp = client.GetAsync(builder.Uri)

            if
                resp.StatusCode = Net.HttpStatusCode.OK
                || resp.StatusCode = Net.HttpStatusCode.Accepted
            then
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"Response List: {str} "

                return JsonSerializer.Deserialize<List>(str, options = serOpts)
            else
                let! str = resp.Content.ReadAsStringAsync()

                if Log.debug_logging then
                    Log.info $"{str} "

                let err =
                    try
                        let err = JsonSerializer.Deserialize<ResponseErrorObj>(str, options = serOpts)
                        Some(ApiError err)
                    with ex ->
                        None

                match err with
                | Some e -> return raise e
                | None -> return failwith $"{str}"
        }

    ///Send a text prompt to create a response, using default values for other request items.
    let createWithDefaults (input: string) =
        create
            ({ Request.Default with
                input =
                    [ IOitem.Message
                          { Message.Default with
                              content = [ Content.Input_text {| text = input |} ] } ] })
            (defaultClient ())
