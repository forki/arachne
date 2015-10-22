﻿//----------------------------------------------------------------------------
//
// Copyright (c) 2014
//
//    Ryan Riley (@panesofglass) and Andrew Cherry (@kolektiv)
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
//----------------------------------------------------------------------------

namespace Arachne.Uri.Template

open System.Text
open FParsec
open Arachne.Core
open Arachne.Uri

(* Data

   Types representing data which may be rendered or extracted
   using UriTemplates. *)

type UriTemplateData =
    | UriTemplateData of Map<UriTemplateKey, UriTemplateValue>

    static member UriTemplateData_ =
        (fun (UriTemplateData x) -> x), (fun x -> UriTemplateData x)

    static member (+) (UriTemplateData a, UriTemplateData b) =
        UriTemplateData (Map.ofList (Map.toList a @ Map.toList b))

and UriTemplateKey =
    | Key of string

and UriTemplateValue =
    | Atom of string
    | List of string list
    | Keys of (string * string) list

    static member Atom_ =
        (function | Atom x -> Some x | _ -> None), (fun x -> Atom x)

    static member List_ =
        (function | List x -> Some x | _ -> None), (fun x -> List x)

    static member Keys_ =
        (function | Keys x -> Some x | _ -> None), (fun x -> Keys x)

(* Matching *)

type Matching<'a,'b> =
    { Match: Match<'a,'b> }

and Match<'a,'b> =
    'a -> Parser<'b, unit>

(* Rendering

   Types and functions to support a general concept of a type rendering
   itself given some state data d', producing a rendering concept much
   like the Format concept, but with readable state. *)

type Rendering<'a> =
    { Render: Render<'a> }

and Render<'a> =
    UriTemplateData -> 'a -> StringBuilder -> StringBuilder

[<AutoOpen>]
module private Functions =

    let match' (m: Match<'a,'b>) s a =
        match run (m a) s with
        | Success (x, _, _) -> x
        | Failure (e, _, _) -> failwith e

    let render (render: Render<'a>) =
        fun d a -> string (render d a (StringBuilder ()))

(* RFC 6570

   Types, parsers and formatters implemented to mirror the specification of 
   URI Template semantics as defined in RFC 6570.

   Taken from [http://tools.ietf.org/html/rfc6570] *)

(* Parsers

   Some extra functions for parsing, in particular for dynamically
   parsing using a list of dynamically constructed parsers which should
   succeed or fail as a single parser. *)

[<AutoOpen>]
module private Parsers =

    let multi parsers =
        fun stream ->
            let rec eval state =
                match state with
                | vs, [] ->
                    Reply (vs)
                | vs, p :: ps ->
                    match p stream with
                    | (x: Reply<'a>) when x.Status = Ok -> eval (x.Result :: vs, ps)
                    | (x) -> Reply<'a list> (Status = x.Status, Error = x.Error)

            eval ([], parsers)

    let multiSepBy parsers sep =
        fun stream ->
            let rec eval state =
                match state with
                | _, vs, [] ->
                    Reply (vs)
                | true, vs, ps ->
                    match sep stream with
                    | (x: Reply<unit>) when x.Status = Ok -> eval (false, vs, ps)
                    | (x) -> Reply<'a list> (Status = x.Status, Error = x.Error)
                | false, vs, p :: ps ->
                    match p stream with
                    | (x: Reply<'a>) when x.Status = Ok -> eval (true, x.Result :: vs, ps)
                    | (x) -> Reply<'a list> (Status = x.Status, Error = x.Error)

            eval (false, [], parsers)

(* Grammar

   NOTE: We do not currently support IRIs - this may
   be supported in future. *)

[<AutoOpen>]
module internal Grammar =

    let isLiteral i =
            i = 0x21
         || i >= 0x23 && i <= 0x24
         || i = 0x26
         || i >= 0x28 && i <= 0x3b
         || i = 0x3d
         || i >= 0x3f && i <= 0x5b
         || i = 0x5d
         || i = 0x5f
         || i >= 0x61 && i <= 0x7a
         || i = 0x7e

    let isVarchar i =
            isAlpha i
         || Grammar.isDigit i
         || i = 0x5f // _

(* Template

   Taken from RFC 6570, Section 2 Syntax
   See [http://tools.ietf.org/html/rfc6570#section-2] *)

type UriTemplate =
    | UriTemplate of UriTemplatePart list

    static member internal Mapping =

        let uriTemplateP =
            many1 UriTemplatePart.Mapping.Parse |>> UriTemplate

        let uriTemplateF =
            function | UriTemplate u -> join UriTemplatePart.Mapping.Format id u

        { Parse = uriTemplateP
          Format = uriTemplateF }

    static member Matching =

        let uriTemplateM =
            function | UriTemplate parts ->
                        multi (List.map UriTemplatePart.Matching.Match parts)
                        |>> List.fold (+) (UriTemplateData Map.empty)

        { Match = uriTemplateM }

    static member Rendering =

        let uriTemplateR (data: UriTemplateData) =
            function | UriTemplate p -> join (UriTemplatePart.Rendering.Render data) id p

        { Render = uriTemplateR }

    static member Format =
        Formatting.format UriTemplate.Mapping.Format

    static member Parse =
        Parsing.parse UriTemplate.Mapping.Parse

    static member TryParse =
        Parsing.tryParse UriTemplate.Mapping.Parse

    static member (+) (UriTemplate x, UriTemplate y) =
        match List.rev x, y with
        | (UriTemplatePart.Literal (Literal x) :: xs),
          (UriTemplatePart.Literal (Literal y) :: ys) ->
            UriTemplate (List.rev xs @ [ UriTemplatePart.Literal (Literal (x + y)) ] @ ys)
        | _ ->
            UriTemplate (x @ y)

    override x.ToString () =
        UriTemplate.Format x

    member x.Match uri =
        match' UriTemplate.Matching.Match uri x

    member x.Render data =
        render UriTemplate.Rendering.Render data x

and UriTemplatePart =
    | Literal of Literal
    | Expression of Expression

    static member internal Mapping =

        let uriTemplatePartP =
            (Expression.Mapping.Parse |>> Expression) <|> (Literal.Mapping.Parse |>> Literal)

        let uriTemplatePartF =
            function | Literal l -> Literal.Mapping.Format l
                     | Expression e -> Expression.Mapping.Format e

        { Parse = uriTemplatePartP
          Format = uriTemplatePartF }

    static member Matching =

        let uriTemplatePartM =
            function | Literal l -> Literal.Matching.Match l
                     | Expression e -> Expression.Matching.Match e

        { Match = uriTemplatePartM }

    static member Rendering =

        let uriTemplatePartR data =
            function | Literal l -> Literal.Rendering.Render data l
                     | Expression e-> Expression.Rendering.Render data e

        { Render = uriTemplatePartR }

    static member Format =
        Formatting.format UriTemplatePart.Mapping.Format

    override x.ToString () =
        UriTemplatePart.Format x

    member x.Match part =
        match' UriTemplatePart.Matching.Match part x

and Literal =
    | Literal of string

    static member internal Mapping =

        let parser =
            PercentEncoding.makeParser isLiteral

        let formatter =
            PercentEncoding.makeFormatter isLiteral

        let literalP =
            notEmpty parser |>> Literal.Literal

        let literalF =
            function | Literal l -> formatter l

        { Parse = literalP
          Format = literalF }

    static member Matching =
        
        let literalM =
            function | Literal l -> pstring l >>% UriTemplateData Map.empty

        { Match = literalM }

    static member Rendering =

        let literalR _ =
            function | Literal l -> append l

        { Render = literalR }

and Expression =
    | Expression of Operator option * VariableList

    static member internal Mapping =

        let expressionP =
            between 
                (skipChar '{') (skipChar '}') 
                (opt Operator.Mapping.Parse .>>. VariableList.Mapping.Parse)
                |>> Expression

        let expressionF =
            function | Expression (Some o, v) ->
                           append "{"
                        >> Operator.Mapping.Format o
                        >> VariableList.Mapping.Format v
                        >> append "}"
                     | Expression (_, v) ->
                           append "{"
                        >> VariableList.Mapping.Format v
                        >> append "}"

        { Parse = expressionP
          Format = expressionF }

    static member Matching =

        (* Primitives *)

        let idP =
            preturn ()

        let simpleP =
            PercentEncoding.makeParser isUnreserved

        let isReserved i =
                isReserved i
             || isUnreserved i

        let reservedP =
            PercentEncoding.makeParser isReserved

        (* Values *)

        let atomP p key =
            p |>> fun s -> key, Atom s

        let listP p sep =
            sepBy p sep |>> List

        let keysP p sep =
            sepBy (p .>> skipChar '=' .>>. p) sep |>> Keys

        let listOrKeysP p sep key =
            attempt (keysP p sep) <|> listP p sep |>> fun v -> key, v

        (* Mapping *)

        let mapVariable key =
            function | None, Some (Level4 Explode) -> listOrKeysP simpleP (skipChar ',') key
                     | None, _ -> atomP simpleP key
                     | Some (Level2 _), Some (Level4 Explode) -> listOrKeysP reservedP (skipChar ',') key
                     | Some (Level2 _), _ -> atomP reservedP key
                     | Some (Level3 Label), Some (Level4 Explode) -> listOrKeysP simpleP (skipChar '.') key
                     | Some (Level3 Label), _ -> atomP simpleP key
                     | Some (Level3 Segment), Some (Level4 Explode) -> listOrKeysP simpleP (skipChar '/') key
                     | Some (Level3 Segment), _ -> atomP simpleP key
                     | _ -> failwith ""

        let mapVariables o (VariableList vs) =
            List.map (fun (VariableSpec (VariableName n, m)) ->
                mapVariable (Key n) (o, m)) vs

        let mapExpression =
                function | Expression (None, vs) -> idP, mapVariables None vs, skipChar ','
                         | Expression (Some (Level2 Reserved), vs) -> idP, mapVariables (Some (Level2 Reserved)) vs, skipChar ','
                         | Expression (Some (Level2 Fragment), vs) -> skipChar '#', mapVariables (Some (Level2 Fragment)) vs, skipChar ','
                         | Expression (Some (Level3 Label), vs) -> skipChar '.', mapVariables (Some (Level3 Label)) vs, skipChar '.'
                         | Expression (Some (Level3 Segment), vs) -> skipChar '/', mapVariables (Some (Level3 Segment)) vs, skipChar '/'
                         | _ -> failwith ""
             >> fun (prefix, keyValuePair, sep) -> prefix >>. multiSepBy keyValuePair sep

        let expressionM e =
            mapExpression e |>> fun vs -> UriTemplateData (Map.ofList vs)

        { Match = expressionM }

    static member Rendering =

        (* Expansion *)

        let crop (s: string) length =
            s.Substring (0, min length s.Length)

        let expandUnary f s =
            function | (_, Atom "", _)
                     | (_, List [], _)
                     | (_, Keys [], _) -> id
                     | (_, Atom a, Some (Level4 (Prefix i))) -> f (crop a i)
                     | (_, Atom a, _) -> f a
                     | (_, List l, Some (Level4 Explode)) -> join f s l
                     | (_, List l, _) -> join f (append ",") l
                     | (_, Keys k, Some (Level4 Explode)) -> join (fun (k, v) -> f k >> append "=" >> f v) s k
                     | (_, Keys k, _) -> join (fun (k, v) -> f k >> append "," >> f v) (append ",") k

        let expandBinary f s omit =
            function | (n, Atom x, _) when omit x -> f n
                     | (n, List [], _)
                     | (n, Keys [], _) -> f n
                     | (n, Atom a, Some (Level4 (Prefix i))) -> f n >> append "=" >> f (crop a i)
                     | (n, Atom a, _) -> f n >> append "=" >> f a
                     | (n, List l, Some (Level4 Explode)) -> join (fun v -> f n >> append "=" >> f v) s l
                     | (n, List l, _) -> f n >> append "=" >> join f (append ",") l
                     | (_, Keys k, Some (Level4 Explode)) -> join (fun (k, v) -> f k >> append "=" >> f v) s k
                     | (n, Keys k, _) -> f n >> append "=" >> join (fun (k, v) -> f k >> append "," >> f v) (append ",") k

        (* Filtering *)

        let choose (VariableList variableList) (UriTemplateData data) =
            variableList
            |> List.map (fun (VariableSpec (VariableName n, m)) ->
                match Map.tryFind (Key n) data with
                | None
                | Some (List [])
                | Some (Keys []) -> None
                | Some v -> Some (n, v, m))
            |> List.choose id

        (* Rendering *)

        let render f variableList data =
            match choose variableList data with
            | [] -> id
            | data -> f data

        let renderUnary prefix item sep =
            render (fun x -> prefix >> join (expandUnary item sep) sep x)

        let renderBinary prefix item sep omit =
            render (fun x -> prefix >> join (expandBinary item sep omit) sep x)

        (* Simple Expansion *)

        let simpleF =
            PercentEncoding.makeFormatter isUnreserved

        let simpleExpansion =
            renderUnary id simpleF (append ",")

        (* Reserved Expansion *)

        let isReserved i =
                isReserved i
             || isUnreserved i

        let reservedF =
            PercentEncoding.makeFormatter isReserved

        let reservedExpansion =
            renderUnary id reservedF (append ",")

        (* Fragment Expansion *)

        let fragmentExpansion =
            renderUnary (append "#") reservedF (append ",")

        (* Label Expansion with Label-Prefix *)

        let labelExpansion =
            renderUnary (append ".") simpleF (append ".")

        (* Path Segment Expansion *)

        let segmentExpansion =
            renderUnary (append "/") simpleF (append "/")

        (* Parameter Expansion *)

        let parameterExpansion =
            renderBinary (append ";") simpleF (append ";") ((=) "")

        (* Query Expansion *)

        let queryExpansion =
            renderBinary (append "?") simpleF (append "&") (fun _ -> false)

        (* Query Continuation Expansion *)

        let queryContinuationExpansion =
            renderBinary (append "&") simpleF (append "&") (fun _ -> false)

        (* Expression *)

        let expressionR data =
            function | Expression (None, v) -> simpleExpansion v data
                     | Expression (Some (Level2 Reserved), v) -> reservedExpansion v data
                     | Expression (Some (Level2 Fragment), v) -> fragmentExpansion v data
                     | Expression (Some (Level3 Label), v) -> labelExpansion v data
                     | Expression (Some (Level3 Segment), v) -> segmentExpansion v data
                     | Expression (Some (Level3 Parameter), v) -> parameterExpansion v data
                     | Expression (Some (Level3 Query), v) -> queryExpansion v data
                     | Expression (Some (Level3 QueryContinuation), v) -> queryContinuationExpansion v data
                     | _ -> id

        { Render = expressionR }

(* Operators

   Taken from RFC 6570, Section 2.2 Expressions
   See [http://tools.ietf.org/html/rfc6570#section-2.2] *)

and Operator =
    | Level2 of OperatorLevel2
    | Level3 of OperatorLevel3
    | Reserved of OperatorReserved

    static member internal Mapping =

        let operatorP =
            choice [
                OperatorLevel2.Mapping.Parse |>> Level2
                OperatorLevel3.Mapping.Parse |>> Level3
                OperatorReserved.Mapping.Parse |>> Reserved ]

        let operatorF =
            function | Level2 o -> OperatorLevel2.Mapping.Format o
                     | Level3 o -> OperatorLevel3.Mapping.Format o
                     | Reserved o -> OperatorReserved.Mapping.Format o

        { Parse = operatorP
          Format = operatorF }

and OperatorLevel2 =
    | Reserved
    | Fragment

    static member internal Mapping =

        let operatorLevel2P =
            choice [
                skipChar '+' >>% Reserved
                skipChar '#' >>% Fragment ]

        let operatorLevel2F =
            function | Reserved -> append "+"
                     | Fragment -> append "#"

        { Parse = operatorLevel2P
          Format = operatorLevel2F }

and OperatorLevel3 =
    | Label
    | Segment
    | Parameter
    | Query
    | QueryContinuation

    static member internal Mapping =

        let operatorLevel3P =
            choice [
                skipChar '.' >>% Label
                skipChar '/' >>% Segment
                skipChar ';' >>% Parameter
                skipChar '?' >>% Query
                skipChar '&' >>% QueryContinuation ]

        let operatorLevel3F =
            function | Label -> append "."
                     | Segment -> append "/"
                     | Parameter -> append ";"
                     | Query -> append "?"
                     | QueryContinuation -> append "&"

        { Parse = operatorLevel3P
          Format = operatorLevel3F }

and OperatorReserved =
    | Equals
    | Comma
    | Exclamation
    | At
    | Pipe

    static member internal Mapping =

        let operatorReservedP =
            choice [
                skipChar '=' >>% Equals
                skipChar ',' >>% Comma
                skipChar '!' >>% Exclamation
                skipChar '@' >>% At
                skipChar '|' >>% Pipe ]

        let operatorReservedF =
            function | Equals -> append "="
                     | Comma -> append ","
                     | Exclamation -> append "!"
                     | At -> append "@"
                     | Pipe -> append "!"

        { Parse = operatorReservedP
          Format = operatorReservedF }

(* Variables

   Taken from RFC 6570, Section 2.3 Variables
   See [http://tools.ietf.org/html/rfc6570#section-2.3] *)

and VariableList =
    | VariableList of VariableSpec list

    static member internal Mapping =

        let variableListP =
            sepBy1 VariableSpec.Mapping.Parse (skipChar ',')
            |>> VariableList

        let variableListF =
            function | VariableList v -> join VariableSpec.Mapping.Format (append ",") v

        { Parse = variableListP
          Format = variableListF }

and VariableSpec =
    | VariableSpec of VariableName * Modifier option

    static member internal Mapping =

        let variableSpecP =
            VariableName.Mapping.Parse .>>. opt Modifier.Mapping.Parse
            |>> VariableSpec

        let variableSpecF =
            function | VariableSpec (name, Some m) ->
                           VariableName.Mapping.Format name
                        >> Modifier.Mapping.Format m
                     | VariableSpec (name, _) ->
                        VariableName.Mapping.Format name

        { Parse = variableSpecP
          Format = variableSpecF }

and VariableName =
    | VariableName of string

    static member internal Mapping =

        // TODO: Assess the potential non-compliance
        // with percent encoding in variable names, especially
        // in cases which could involve percent encoded "." characters,
        // which would not play well with our over-naive formatting here
        // (which should potentially be reworked, although we are trying
        // to avoid keys having list values...)

        let parser =
            PercentEncoding.makeParser isVarchar

        let formatter =
            PercentEncoding.makeFormatter isVarchar

        let variableNameP =
            sepBy1 (notEmpty parser) (skipChar '.')
            |>> ((String.concat ".") >> VariableName)

        let variableNameF =
            function | VariableName n ->
                        join formatter (append ".") (List.ofArray (n.Split ([| '.' |])))

        { Parse = variableNameP
          Format = variableNameF }

(* Modifiers

   Taken from RFC 6570, Section 2.4 Value Modifiers
   See [http://tools.ietf.org/html/rfc6570#section-2.4] *)

and Modifier =
    | Level4 of ModifierLevel4

    static member internal Mapping =

        let modifierP =
            ModifierLevel4.Mapping.Parse |>> Level4

        let modifierF =
            function | Level4 m -> ModifierLevel4.Mapping.Format m

        { Parse = modifierP
          Format = modifierF }

and ModifierLevel4 =
    | Prefix of int
    | Explode

    static member internal Mapping =

        let modifierLevel4P =
            choice [
                skipChar ':' >>. pint32 |>> Prefix
                skipChar '*' >>% Explode ]

        let modifierLevel4F =
            function | Prefix i -> appendf1 ":{0}" i
                     | Explode -> append "*"

        { Parse = modifierLevel4P
          Format = modifierLevel4F }