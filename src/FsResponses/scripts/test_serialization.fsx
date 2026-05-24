
#load "packages.fsx"
open System.Net.Http.Headers
open System.Threading.Tasks
open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Nodes
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.Json.Serialization
open FsResponses

let jsonObt = """
{
  "id": "resp_68120cfbf7348191bbd6a9f9edb77b4f0b27122f87039991",
  "object": "response",
  "created_at": 1746013436,
  "status": "completed",
  "error": null,
  "incomplete_details": null,
  "instructions": "Find how to best do async in F# from bing.com",
  "max_output_tokens": null,
  "model": "computer-use-preview-2025-03-11",
  "output": [
    {
      "id": "rs_68120cff59f881918b745e7c6502c5020b27122f87039991",
      "type": "reasoning",
      "summary": []
    },
    {
      "id": "cu_68120cffd07c8191bf7d024ffc366c7d0b27122f87039991",
      "type": "computer_call",
      "status": "completed",
      "action": {
        "type": "click",
        "button": "left",
        "x": 170,
        "y": 345
      },
      "call_id": "call_52E4XSBRAG6tXw4KmcC5TUt4",
      "pending_safety_checks": []
    }
  ],
  "parallel_tool_calls": false,
  "previous_response_id": null,
  "reasoning": {
    "effort": "medium",
    "generate_summary": null,
    "summary": null
  },
  "service_tier": "default",
  "store": false,
  "temperature": 1.0,
  "text": {
    "format": {
      "type": "text"
    }
  },
  "tool_choice": "auto",
  "tools": [
    {
      "type": "computer_use_preview",
      "display_height": 997,
      "display_width": 1212,
      "environment": "browser"
    }
  ],
  "top_p": 1.0,
  "truncation": "auto",
  "usage": {
    "input_tokens": 1358,
    "input_tokens_details": {
      "cached_tokens": 0
    },
    "output_tokens": 64,
    "output_tokens_details": {
      "reasoning_tokens": 0
    },
    "total_tokens": 1422
  },
  "user": null,
  "metadata": {}
}
"""

let jobj2 = """
      {
        "id": "resp_6834f66d880c819884c3942c3bf44202009405c07c02905e",
        "object": "response",
        "created_at": 1748301421,
        "status": "completed",
        "background": false,
        "error": null,
        "incomplete_details": null,
        "instructions": null,
        "max_output_tokens": null,
        "model": "gpt-4.1-2025-04-14",
        "output": [
          {
            "id": "msg_6834f66f24d88198810bf3e3d95e559f009405c07c02905e",
            "type": "message",
            "status": "completed",
            "content": [
              {
                "type": "output_text",
                "annotations": [],
                "text": "The image is a screenshot of the T-Mobile website homepage. Here are the main elements visible:\n\n1. **Header Section:**\n   - The top menu bar contains the T-Mobile logo on the far left, followed by navigation links like \"Plans,\" \"Phones & devices,\" \"Deals,\" \"Coverage,\" and \"Join Us.\" \n   - On the right side of the header, there are icons for shopping cart, chat, and user account, with a button labeled \"My account.\"\n\n2. **Promotional Banner:**\n   - A primary headline in the center reads: \"Shop online and save an extra $35 off your new phone.\"\n   - Subtext: \"Free two-day shipping when you buy online. Or call 855-745-1056.\"\n\n3. **Main Promotional Section:**\n   - On the left: \"Best iPhone Offer Ever iPhone 16 Pro On Us No Trade-In Needed,\" with the image of an iPhone.\n   - On the right: \"Plus, we\u2019ll pay off your current phone up to $800 a line.\" There is a brief explanatory text about paying off your current phone via virtual prepaid Mastercard and an offer for an iPhone 16 Pro with a new plan.\n   - There is a magenta \"Switch now\" button for user interaction.\n\n4. **Pop-ups and Notices:**\n   - **Survey Pop-up:** \"Find your ideal phone and plan. Answer a few quick questions.\" with options \"Not Now\" or \"Get Started.\"\n   - **T-Mobile Notice at the bottom left:** Explains the use of cookies and tracking, offering \"Accept\" and \"Reject\" buttons.\n   - **Chat With Us Bubble:** In the lower right, a magenta chat bubble says: \"Chat with us\".\n\nThe color theme is predominantly T-Mobile's magenta and white/black. The page is oriented toward enticing new customers with strong promotional offers and making it easy to contact or interact with T-Mobile online."
              }
            ],
            "role": "assistant"
          }
        ],
        "parallel_tool_calls": false,
        "previous_response_id": null,
        "reasoning": {
          "effort": null,
          "summary": null
        },
        "service_tier": "default",
        "store": false,
        "temperature": 1.0,
        "text": {
          "format": {
            "type": "text"
          }
        },
        "tool_choice": "auto",
        "tools": [],
        "top_p": 1.0,
        "truncation": "auto",
        "usage": {
          "input_tokens": 1115,
          "input_tokens_details": {
            "cached_tokens": 0
          },
          "output_tokens": 402,
          "output_tokens_details": {
            "reasoning_tokens": 0
          },
          "total_tokens": 1517
        },
        "user": null,
        "metadata": {}
      } 
"""

let runT (t:Task<'a>) = t.Result

let serOpts = 
    let opts = 
        JsonFSharpOptions.Default()
            .WithUnionInternalTag()
            .WithUnionTagName("type")
            .WithUnionUnwrapRecordCases()
            .WithUnionTagCaseInsensitive()     
            .WithAllowNullFields()
            .WithAllowOverride()
            //.WithSkippableOptionFields(true)
            .ToJsonSerializerOptions()
    opts.WriteIndented <- true
    opts

let test2() =
    let o = JsonSerializer.Deserialize<Response>(jobj2,options=serOpts)
    ()


let test() =
    let respDser = JsonSerializer.Deserialize<Response>(jsonObt, options=Api.serOpts)


    let resp = Api.createWithDefaults "write a haiku about F#" |> runT

    let resp2 = 
        let client = Api.defaultClient()
        let cont = Input_text {|text = "List good F# learning resources" |}
        let input = { Message.Default with content=[cont]}
        let req = {Request.Default with input = [Message input]; tools=[Tool.DefaultWebSearch]}
        client |> Api.create req |> runT

    RUtils.outputText resp2 |> printfn "%s"

    let resp3 = 
        let image = File.ReadAllBytes(@"C:\Users\Faisa\Pictures\Screenshots\Screenshot 2024-09-20 061619.png") |> RUtils.toImageUri
        let client = Api.defaultClient()
        let cont = Input_text {|text = "Describe the image" |}
        let contImg = Input_image {|image_url = image|}
        let input = { Message.Default with content=[cont; contImg]}
        let req = {Request.Default with input = [Message input]}
        client |> Api.create req |> runT

    RUtils.outputText resp3 |> printfn "%s"

    let resp4  = 
        let image = File.ReadAllBytes(@"C:\Users\Faisa\Pictures\Screenshots\Bing.png") 
        let imUrl = image |> RUtils.toImageUri
        let ms = new MemoryStream(image)
        let i2 = System.Drawing.Image.FromStream(ms)
        let client = Api.defaultClient()
        let contImg = Input_image {|image_url = imUrl|}
        let input = { Message.Default with content=[contImg]}
        let tool = Tool_Computer_use {|display_height=int i2.PhysicalDimension.Height; display_width = int i2.PhysicalDimension.Width; environment = ComputerEnvironment.browser|}
        let req = {Request.Default with 
                    input = [Message input]; tools=[tool]; 
                    instructions= Some "Find how to best do async in F# from bing.com"; 
                    model=Models.computer_use_preview
                    truncation = Some "auto"
                }
        client |> Api.create req |> runT

    ()


let(==) (a:string) (b:string) = a.Equals(b, StringComparison.OrdinalIgnoreCase)
"Enter" == "ENTER"
