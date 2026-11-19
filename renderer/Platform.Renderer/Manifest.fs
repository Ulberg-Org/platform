/// Kubernetes manifests as plain data (D62): a small YAML tree built by pure code and written out at the edge.
module Platform.Renderer.Manifest

open System.Collections.Generic
open YamlDotNet.Serialization

type Node =
    | Text of string
    | Number of int
    | Flag of bool
    | Items of Node list
    | Fields of (string * Node) list

let text = Text
let fields = Fields
let items = Items

/// One manifest file: its name in the output folder and its content.
type Manifest = { file: string; content: Node }

let rec private toObj (node: Node) : obj =
    match node with
    | Text s -> s :> obj
    | Number n -> n :> obj
    | Flag b -> b :> obj
    | Items xs -> ResizeArray(xs |> List.map toObj) :> obj
    | Fields fs -> Dictionary<string, obj>(fs |> List.map (fun (k, v) -> KeyValuePair(k, toObj v))) :> obj

/// The manifest as YAML text, keys in the order they were declared.
let toYaml (node: Node) : string =
    SerializerBuilder().Build().Serialize(toObj node)
