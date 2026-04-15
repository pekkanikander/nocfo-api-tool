module Nocfo.JsonHelpers

open System
open System.Text.Json
open System.Text.Json.Nodes
open NocfoApi.Http

/// Deserialise a JSON string to a given CLR type using the generated serialiser options.
let deserializeUntyped (json: string) (t: Type) : obj =
    JsonSerializer.Deserialize(json, t, Serializer.options)

/// Serialise any CLR value to a JSON string using the generated serialiser options.
let serializeUntyped (value: obj) : string =
    JsonSerializer.Serialize(value, value.GetType(), Serializer.options)

/// Build a standalone JsonElement representing a JSON string value.
let jsonString (s: string) : JsonElement =
    JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone()

/// Parse a CSV cell as a JsonElement using permissive semantics:
/// cells starting with '{' or '[' are parsed as JSON objects/arrays;
/// any other non-empty cell becomes a JSON string element.
let parseCsvJsonElement (rawText: string) : JsonElement =
    let s = rawText.Trim()
    if s.StartsWith("{") || s.StartsWith("[") then
        JsonDocument.Parse(s).RootElement.Clone()
    else
        jsonString rawText

/// Clone a JsonElement to produce a standalone value independent of its source JsonDocument.
let cloneElement (element: JsonElement) : JsonElement =
    element.Clone()

/// Return the compact raw JSON text for a JsonElement.
let elementToCompactString (element: JsonElement) : string =
    element.GetRawText()

/// Comparison key for a JsonElement.
/// JSON string values compare by their string content (GetString());
/// all other kinds compare by raw JSON text.
let elementAsComparisonKey (element: JsonElement) : string =
    if element.ValueKind = JsonValueKind.String then
        element.GetString()
    else
        element.GetRawText()

/// Convert a JsonElement to a mutable JsonNode for in-place editing.
let elementToNode (element: JsonElement) : JsonNode =
    match JsonNode.Parse(element.GetRawText()) with
    | null -> failwithf "Cannot represent JSON null as a JsonNode"
    | node -> node

/// Convert a JsonNode back to a standalone, cloned JsonElement.
let nodeToElement (node: JsonNode) : JsonElement =
    JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()
