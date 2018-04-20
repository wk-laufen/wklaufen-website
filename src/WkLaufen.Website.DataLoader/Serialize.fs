module Serialize

open System
open DataModels

let date (d: DateTime) =
    sprintf
        "System.DateTime(%d, %d, %d, %d, %d, %d, %d, %s)"
        d.Year
        d.Month
        d.Day
        d.Hour
        d.Minute
        d.Second
        d.Millisecond
        (sprintf "%s.%O" typeof<DateTimeKind>.FullName d.Kind)

let dateOption = function
    | Some d -> date d |> sprintf "%s |> Some"
    | None -> "None"

let stringOption = function
    | Some v -> sprintf "Some \"%s\"" v
    | None -> "None"

let string =
    sprintf "\"\"\"%s\"\"\""

let stringSeq lines =
    [
        yield "["
        yield! lines |> List.map (string >> sprintf "  %s")
        yield "]"
    ]

let activityTimestamp = function
    | DateTime d -> date d |> sprintf "%s |> DateTime"
    | Date d -> date d |> sprintf "%s |> Date"

let activityTimestampOption = function
    | Some v -> activityTimestamp v |> sprintf "%s |> Some"
    | None -> "None"
