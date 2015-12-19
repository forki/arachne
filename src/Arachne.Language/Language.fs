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

module Arachne.Language

open System.Runtime.CompilerServices
open Arachne.Core
open FParsec

(* Internals *)

[<assembly:InternalsVisibleTo ("Arachne.Http")>]
do ()

(* RFC 5646

   Types, parsers and formatters implemented to mirror the specification of 
   Language Tag semantics as defined in RFC 5646.

   Taken from [http://tools.ietf.org/html/rfc5646] *)

(* Note: The current implementation does not implement either private use
   tags or grandfathered tags. Contributions are welcome if this is a real
   world issue for anyone.

   In addition, the main implementation of a language tag does not implement
   the "extension" property, or the optional "privateuse" property. As the RFC,
   even in the list of example language tags, never produces an example of either
   of these in use they have been assumed to be of low importance for now.

   However, if someone does show a valid and even slightly common use case,
   they will be implemented. *)

[<AutoOpen>]
module internal Grammar =

    let isAlphaDigit i =
            isAlpha i
         || Grammar.isDigit i

    let alphaP min max =
            manyMinMaxSatisfy min max (int >> isAlpha) 
       .>>? notFollowedBy (skipSatisfy (int >> isAlpha))

    let digitP min max =
            manyMinMaxSatisfy min max (int >> isAlpha) 
       .>>? notFollowedBy (skipSatisfy (int >> isAlpha))

    let alphaNumP min max =
            manyMinMaxSatisfy min max (int >> isAlphaDigit) 
       .>>? notFollowedBy (skipSatisfy (int >> isAlphaDigit))

(* Language *)

type Language =
    | Language of string * string list option

    static member internal Mapping =

        let extP =
            skipChar '-' >>. alphaP 3 3

        let extLangP =
            choice [
                attempt (tuple3 extP extP extP) |>> fun (a, b, c) -> a :: b :: [ c ]
                attempt (tuple2 extP extP) |>> fun (a, b) -> a :: [ b ]
                extP |>> fun a -> [ a ] ]

        let languageP =
            choice [
                alphaP 2 3 .>>. opt (attempt extLangP) |>> Language
                alphaP 4 4 |>> (fun x -> Language (x, None))
                alphaP 5 8 |>> (fun x -> Language (x, None)) ]

        let extF =
            function | x -> append "-" >> append x

        let extLangF =
            function | xs -> join extF id xs

        let languageF =
            function | Language (x, Some e) -> append x >> extLangF e
                     | Language (x, _) -> append x 

        { Parse = languageP
          Format = languageF }

    static member format =
        Mapping.format Language.Mapping

    static member parse =
        Mapping.parse Language.Mapping

    static member tryParse =
        Mapping.tryParse Language.Mapping

    override x.ToString () =
        Language.format x

(* Language Tag *)

type LanguageTag =
    | LanguageTag of Language * Script option * Region option * Variant

    static member internal Mapping =

        let languageTagP =
            tuple4 Language.Mapping.Parse 
                   (opt (attempt Script.Mapping.Parse))
                   (opt (attempt Region.Mapping.Parse))
                   (Variant.Mapping.Parse)
            |>> fun (language, script, region, variant) ->
                LanguageTag (language, script, region, variant)

        let languageTagF =
            function | LanguageTag (language, script, region, variant) ->
                         let formatters =
                            [ Language.Mapping.Format language
                              (function | Some x -> Script.Mapping.Format x | _ -> id) script
                              (function | Some x -> Region.Mapping.Format x | _ -> id) region
                              Variant.Mapping.Format variant ]

                         fun b -> List.fold (|>) b formatters

        { Parse = languageTagP
          Format = languageTagF }

    static member format =
        Mapping.format LanguageTag.Mapping

    static member parse =
        Mapping.parse LanguageTag.Mapping

    static member tryParse =
        Mapping.tryParse LanguageTag.Mapping

    override x.ToString () =
        LanguageTag.format x

(* Script *)

 and Script =
    | Script of string

    static member internal Mapping =

        let scriptP =
            skipChar '-' >>. alphaP 4 4 |>> Script

        let scriptF =
            function | Script x -> append "-" >> append x

        { Parse = scriptP
          Format = scriptF }

(* Region *)

 and Region =
    | Region of string

    static member internal Mapping =

        let regionP =
            skipChar '-' >>. (alphaP 2 2 <|> digitP 3 3) |>> Region

        let regionF =
            function | Region x -> append "-" >> append x

        { Parse = regionP
          Format = regionF }

(* Variant *)

 and Variant =
    | Variant of string list

    static member internal Mapping =

        let alphaPrefixVariantP =
            alphaNumP 5 8

        let digitPrefixVariantP =
            satisfy isDigit .>>. alphaNumP 3 3 |>> fun (c, s) -> sprintf "%c%s" c s

        let varP =
            skipChar '-' >>. (alphaPrefixVariantP <|> digitPrefixVariantP)

        let variantP =
            many varP |>> Variant

        let varF =
            function | x -> append "-" >> append x

        let variantF =
            function | Variant xs -> join varF id xs

        { Parse = variantP
          Format = variantF }

(* RFC 4647

   Types, parsers and formatters implemented to mirror the specification of 
   Language Range semantics as defined in RFC 4647.

   Taken from [http://tools.ietf.org/html/rfc4647] *)

type LanguageRange =
    | Range of string list
    | Any

    static member internal Mapping =

        let languageRangeP =
            choice [
                skipChar '*' >>% Any
                alphaP 1 8 .>>. many (skipChar '-' >>. alphaNumP 1 8) |>> (fun (x, xs) -> Range (x :: xs)) ]


        let languageRangeF =
            function | Range x -> join append (append "-") x
                     | Any -> append "*"

        { Parse = languageRangeP
          Format = languageRangeF }

    static member format =
        Mapping.format LanguageRange.Mapping

    static member parse =
        Mapping.parse LanguageRange.Mapping

    static member tryParse =
        Mapping.tryParse LanguageRange.Mapping

    override x.ToString () =
        LanguageRange.format x
