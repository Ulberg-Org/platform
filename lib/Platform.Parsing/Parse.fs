/// Small, reusable parsers for turning DTO fields into domain values. Each returns a Result with a
/// message; callers combine them with FsToolkit's Validation so every problem is reported at once.
/// See docs/fsharp-style.md.
module Platform.Parse

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open YamlDotNet.Core
open YamlDotNet.Serialization

let required (field: string) (x: 'a option) : Result<'a, string> =
    x |> Result.requireSome $"'{field}' is missing"

/// A DTO string that YAML left out arrives empty-handed (System.Text.Json fills a missing member with its
/// default), so it's reported as missing before any check looks inside it.
let present (field: string) (s: string) : Result<string, string> =
    if String.IsNullOrEmpty s then
        Error $"'{field}' is missing"
    else
        Ok s

let notEmpty (field: string) (s: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace s then
        Error $"'{field}' is empty"
    else
        Ok s

let guid (field: string) (s: string) : Result<Guid, string> =
    match Guid.TryParse s with
    | true, g -> Ok g
    | _ -> Error $"'{field}': '{s}' is not an object ID"

let matches (pattern: Regex) (field: string) (s: string) : Result<string, string> =
    let valid (s: string) =
        if pattern.IsMatch s then
            Ok s
        else
            Error $"'{field}': '{s}' is not valid"

    present field s |> Result.bind valid

let between (field: string) (low: int) (high: int) (n: int) : Result<int, string> =
    if n >= low && n <= high then
        Ok n
    else
        Error $"'{field}': {n} is not between {low} and {high}"

let atLeast (field: string) (low: int) (n: int) : Result<int, string> =
    if n >= low then
        Ok n
    else
        Error $"'{field}': {n} is below {low}"

let startsWith (field: string) (prefix: string) (s: string) : Result<string, string> =
    let starts (s: string) =
        if s.StartsWith prefix then
            Ok s
        else
            Error $"'{field}': '{s}' must start with {prefix}"

    present field s |> Result.bind starts

let equals (field: string) (expected: string) (s: string) : Result<unit, string> =
    if s = expected then
        Ok()
    else
        Error $"'{field}': '{s}' is not {expected}"

/// `value` must not be the same as the field `otherField`'s value.
let differs (field: string) (otherField: string) (other: 'a) (value: 'a) : Result<'a, string> =
    if value <> other then
        Ok value
    else
        Error $"'{field}' must differ from '{otherField}'"

/// Prefixes every error with where it happened, e.g. "groups.app-developers: unknown person 'carl'".
let within (context: string) (v: Validation<'a, string>) : Validation<'a, string> =
    v |> Validation.mapError (fun e -> $"{context}: {e}")

// YAML scalars arrive as strings after the YAML → JSON step, so numbers are also read from strings.
let private json =
    JsonSerializerOptions(
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    )

/// Reads YAML into a DTO: YAML → plain data → JSON → DTO. System.Text.Json maps F# records, options,
/// lists and maps natively and rejects unknown keys, so a misspelled section is an error, not ignored.
let yaml<'dto> (text: string) : Validation<'dto, string> =
    try
        let data = DeserializerBuilder().Build().Deserialize<obj> text
        let asJson = SerializerBuilder().JsonCompatible().Build().Serialize data

        // System.Text.Json returns null for an empty document. Option.ofObj turns that into None right at
        // the library boundary, so no null reaches our code.
        JsonSerializer.Deserialize(asJson, typeof<'dto>, json)
        |> Option.ofObj
        |> Option.map unbox<'dto>
        |> Result.requireSome "the file is empty"
        |> Validation.ofResult
    with
    | :? YamlException as ex -> Validation.error $"not valid YAML at line {ex.Start.Line}: {ex.Message}"
    | :? JsonException as ex -> Validation.error $"unexpected content: {ex.Message}"
