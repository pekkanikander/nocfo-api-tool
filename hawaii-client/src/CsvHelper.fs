module Nocfo.CsvHelpers
// Minimal CsvHelper support for F#-specific shapes (option<'a>, list<'a>, and raw JSON
// via System.Text.Json.JsonElement).
// Usage:
//   #r "nuget: CsvHelper, 33.0.0"
//   #load "src/CsvHelper.fs"
//   open Nocfo.CsvHelpers
//   use csv = new CsvWriter(writer, CsvConfiguration(CultureInfo.InvariantCulture))
//   registerFSharpConvertersFor csv (typeof<MyRecord>)
//   csv.WriteHeader<MyRecord>(); csv.NextRecord()
//   csv.WriteRecord(instance); csv.NextRecord()

open System
open System.Reflection
open System.Globalization
open System.Text.Json
open Microsoft.FSharp.Reflection
open CsvHelper
open CsvHelper.Configuration
open CsvHelper.TypeConversion
open Nocfo.JsonHelpers

// --- Shape detection helpers ---

let private isOption (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

let private optionInner (t: Type) = t.GetGenericArguments().[0]

let private isFSharpList (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>

let private listInner (t: Type) = t.GetGenericArguments().[0]

// --- Converters ---

type OptionConverter<'a>() =
    inherit DefaultTypeConverter()
    override _.ConvertToString(value, memberMapData, row) =
        if isNull value then "" else
        let t = value.GetType()
        let (case, fields) = FSharpValue.GetUnionFields(value, t)
        if case.Name = "Some" then
            match box fields.[0] with
            | null -> ""
            | :? IFormattable as f -> f.ToString(null, CultureInfo.InvariantCulture)
            | :? string as s -> s
            | :? JsonElement as e -> elementToCompactString e
            | v -> v.ToString()
        else ""

type SeqJoinConverter<'a>() =
    inherit DefaultTypeConverter()
    override _.ConvertToString(value, memberMapData, row) =
        match value with
        | :? System.Collections.IEnumerable as xs ->
            xs
            |> Seq.cast<obj>
            |> Seq.map (function
                | null -> ""
                | :? IFormattable as f -> f.ToString(null, CultureInfo.InvariantCulture)
                | :? string as s -> s
                | :? JsonElement as e -> elementToCompactString e
                | v -> serializeUntyped v)
            |> String.concat ";"
        | _ -> ""

type JsonElementCompactConverter() =
    inherit DefaultTypeConverter()
    override _.ConvertToString(value, memberMapData, row) =
        match value with
        | :? JsonElement as e -> elementToCompactString e
        | null -> ""
        | v -> v.ToString()

/// Inspect the target type and register F#-aware converters for its property types.
/// This avoids CsvHelper's AutoMap (which can trip on F# shapes via constructor parameter mapping).
let registerFSharpConvertersFor (context: CsvContext) (t: Type) =
    let cache = context.TypeConverterCache
    let registerForType (pt: Type) =
        if isOption pt then
            let inner = optionInner pt
            let convT = typedefof<OptionConverter<_>>.MakeGenericType [| inner |]
            let conv  = Activator.CreateInstance(convT) :?> ITypeConverter
            cache.AddConverter(pt, conv) |> ignore
        elif isFSharpList pt then
            let inner = listInner pt
            let convT = typedefof<SeqJoinConverter<_>>.MakeGenericType [| inner |]
            let conv  = Activator.CreateInstance(convT) :?> ITypeConverter
            cache.AddConverter(pt, conv) |> ignore
        elif pt = typeof<JsonElement> then
            cache.AddConverter(pt, JsonElementCompactConverter()) |> ignore
        else
            ()

    // Register converters for all public instance properties on the type
    for p in t.GetProperties(BindingFlags.Instance ||| BindingFlags.Public) do
        registerForType p.PropertyType
