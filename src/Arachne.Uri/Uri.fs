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

namespace Arachne.Uri

open System
open System.Net
open System.Net.Sockets
open System.Runtime.CompilerServices
open System.Text
open Arachne.Core
open FParsec

(* Internals *)

[<assembly:InternalsVisibleTo ("Arachne.Http")>]
[<assembly:InternalsVisibleTo ("Arachne.Http.Cors")>]
[<assembly:InternalsVisibleTo ("Arachne.Uri.Template")>]
do ()

(* RFC 3986

   Types, parsers and formatters implemented to mirror the specification of 
   URI semantics as defined in RFC 3986.

   Taken from [http://tools.ietf.org/html/rfc3986] *)

[<AutoOpen>]
module internal Grammar =

    let isUnreserved i =
            isAlpha i
         || Grammar.isDigit i
         || i = 0x2d // -
         || i = 0x2e // .
         || i = 0x5f // _
         || i = 0x7e // ~

    let isGenDelim i =
            i = 0x3a // :
         || i = 0x2f // /
         || i = 0x3f // ?
         || i = 0x23 // #
         || i = 0x5b // [
         || i = 0x5d // ]
         || i = 0x40 // @

    let isSubDelim i =
            i = 0x21 // !
         || i = 0x24 // $
         || i = 0x26 // &
         || i = 0x5c // \
         || i >= 0x28 && i <= 0x2c //   * + ,
         || i = 0x3b // ;
         || i = 0x3d // =

    let isReserved i=
            isGenDelim i
         || isSubDelim i

(* Percent-Encoding

   Code for percent-encoding data given some simple assumptions about what
   should be allowed through unencoded. *)

[<RequireQualifiedAccess>]
module PercentEncoding =

    (* Grammar *)

    let private pct =
        byte 0x25

    (* UTF-8

       Shorthand for UTF-8 encoding and decoding of strings (given
       the assumption that the .NET UTF-16/Unicode string is our
       basic string type). *)

    let private toBytes : string -> byte list =
        Encoding.UTF8.GetBytes >> List.ofArray

    let private toString : byte list -> string =
        List.toArray >> Encoding.UTF8.GetString

    (* Indices

       Simple lookups/indices for converting between bytes and the hex
       encoding of those bytes. *)

    let private hex =
        [ 0x00 .. 0xff ]
        |> List.map byte
        |> List.map (fun i -> i, toBytes (i.ToString "X2"))

    let private byteIndex =
        hex
        |> Map.ofList

    let private hexIndex =
        hex
        |> List.map (fun (a, b) -> (b, a))
        |> Map.ofList

    (* Parsing

       Parsing functions, providing a function to create a parser
       given a whitelist of allowed characters within the input (pct-encoded
       values are implicitly allowed, and converted to their Unicode/UTF-16
       form). *)

    let private hexdigP =
        satisfy (int >> isHexdig)

    let private pctP =
        skipChar '%' >>. hexdigP .>>. hexdigP
        |>> fun (a, b) ->
            char (Map.find [ byte a; byte b ] hexIndex)

    let makeParser p =
        many (attempt pctP <|> satisfy (int >> p))
        |>> fun x ->
            new String (List.toArray x)

    (* Formatting

       Formatting functions, providing a function to create an formatter
       given a whitelist set of allowed characters within the encoded
       output. *)

    let private hexdig =
        int >> isHexdig

    let private format p =
        let rec format r =
            function | [] -> r
                     | h :: x :: y :: t when h = pct && hexdig x && hexdig y -> format (r @ [ h; x; y ]) t
                     | h :: t when p (int h) -> format (r @ [ h ]) t
                     | h :: t -> format (r @ [ pct ] @ Map.find h byteIndex) t

        format []

    let makeFormatter res =
        toBytes >> format res >> toString >> append

(* Scheme

   Taken from RFC 3986, Section 3.1 Scheme
   See [http://tools.ietf.org/html/rfc3986#section-3.1] *)

(* Section 3.1 *)

type Scheme =
    | Scheme of string

    static member internal Mapping =

        let isSchemeChar i =
                isAlpha i
             || Grammar.isDigit i
             || i = 0x2b // +
             || i = 0x2d // -
             || i = 0x2e // .

        let schemeP =
            satisfy (int >> isAlpha) .>>. manySatisfy (int >> isSchemeChar)
            |>> ((fun (x, xs) -> sprintf "%c%s" x xs) >> Scheme)

        let schemeF =
            function | Scheme x -> append x

        { Parse = schemeP
          Format = schemeF }

    static member Format =
        Formatting.format Scheme.Mapping.Format

    static member Parse =
        Parsing.parse Scheme.Mapping.Parse

    static member TryParse =
        Parsing.tryParse Scheme.Mapping.Parse

    override x.ToString () =
        Scheme.Format x

(* Authority

   Taken from RFC 3986, Section 3.2 Authority
   See [http://tools.ietf.org/html/rfc3986#section-3.2] *)

(* Section 3.2 *)

type Authority =
    | Authority of Host * Port option * UserInfo option

    static member internal Mapping =

        let authorityP =
                 opt (attempt (UserInfo.Mapping.Parse .>> skipChar '@')) 
            .>>. Host.Mapping.Parse 
            .>>. opt Port.Mapping.Parse
             |>> fun ((user, host), port) -> Authority (host, port, user)

        let authorityF =
            function | Authority (h, p, u) ->
                        let formatters =
                            [ (function | Some u -> UserInfo.Mapping.Format u >> append "@"
                                        | _ -> id) u
                              Host.Mapping.Format h
                              (function | Some p -> Port.Mapping.Format p 
                                        | _ -> id) p ]

                        fun b -> List.fold (|>) b formatters

        { Parse = authorityP
          Format = authorityF }

    static member Format =
        Formatting.format Authority.Mapping.Format

    static member Parse =
        Parsing.parse Authority.Mapping.Parse
    
    static member TryParse =
        Parsing.tryParse Authority.Mapping.Parse

    override x.ToString () =
        Authority.Format x

(* Section 3.2.1 *)

and UserInfo =
    | UserInfo of string

    static member internal Mapping =

        let isUserInfoChar i =
                isUnreserved i
             || isSubDelim i
             || i = 0x3a // :

        let parser =
            PercentEncoding.makeParser isUserInfoChar

        let formatter =
            PercentEncoding.makeFormatter isUserInfoChar

        let userInfoP =
            notEmpty parser |>> UserInfo

        let userInfoF =
            function | UserInfo x -> formatter x

        { Parse = userInfoP
          Format = userInfoF }

(* Section 3.2.2 *)

(* Note: In this instance we use the built in IP Address type and parser
   as the standard is implemented fully and seems to handle all suitable cases.

   We also make a slight restriction for practicality at the moment on
   implementing IP-literal as an IPv6 specific type, discarding IPvFuture. As
   it stands, that's unlikely to be an issue, but could perhaps be revisited. *)

and Host =
    | IPv4 of IPAddress
    | IPv6 of IPAddress
    | Name of RegName

    static member internal Mapping =

        let isIpv6Char i =
                isHexdig i
             || i = 0x3a // :

        let ipv6AddressP =
            skipChar '[' >>. (many1Satisfy (int >> isIpv6Char) >>= (fun x ->
                match IPAddress.TryParse x with
                | true, x when x.AddressFamily = AddressFamily.InterNetworkV6 -> preturn (IPv6 x)
                | _ -> pzero)) .>> skipChar ']'

        let isIpv4Char i =
                Grammar.isDigit i
             || i = 0x2e // .

        let ipv4AddressP =
            many1Satisfy (int >> isIpv4Char) >>= (fun x ->
                match IPAddress.TryParse x with
                | true, x when x.AddressFamily = AddressFamily.InterNetwork -> preturn (IPv4 x)
                | _ -> pzero)

        let hostP =
            choice [
                attempt ipv6AddressP
                attempt ipv4AddressP
                RegName.Mapping.Parse |>> Name ]

        let hostF =
            function | IPv4 x -> append (string x)
                     | IPv6 x -> append "[" >> append (string x) >> append "]"
                     | Name x -> RegName.Mapping.Format x

        { Parse = hostP
          Format = hostF }

and RegName =
    | RegName of string

    static member internal Mapping =

        let isRegNameChar i =
                isUnreserved i
             || isSubDelim i

        let parser =
            PercentEncoding.makeParser isRegNameChar

        let formatter =
            PercentEncoding.makeFormatter isRegNameChar

        let regNameP =
            notEmpty parser |>> RegName

        let regNameF =
            function | RegName x -> formatter x

        { Parse = regNameP
          Format = regNameF }

and Port =
    | Port of int

    static member internal Mapping =

        let portP =
                skipChar ':' >>. puint32 
            |>> (int >> Port)

        let portF =
            function | Port x -> append ":" >> append (string x)

        { Parse = portP
          Format = portF }

(* Path

   Taken from RFC 3986, Section 3.3 Path
   See [http://tools.ietf.org/html/rfc3986#section-3.3] *)

[<AutoOpen>]
module private Path =

    let isPcharNc i =
            isUnreserved i
         || isSubDelim i
         || i = 0x40 // @

    let isPchar i =
            isPcharNc i
         || i = 0x3a // :

    let pcharParser =
        PercentEncoding.makeParser isPchar

    let pcharNcParser =
        PercentEncoding.makeParser isPcharNc

    let pcharFormatter =
        PercentEncoding.makeFormatter isPchar

(* Absolute Or Empty *)

type PathAbsoluteOrEmpty =
    | PathAbsoluteOrEmpty of string list

    static member internal Mapping =

        let pathAbsoluteOrEmptyP =
                many (skipChar '/' >>. pcharParser) 
            |>> PathAbsoluteOrEmpty

        let pathAbsoluteOrEmptyF =
            function | PathAbsoluteOrEmpty [] -> id
                     | PathAbsoluteOrEmpty xs -> append "/" >> join pcharFormatter (append "/") xs

        { Parse = pathAbsoluteOrEmptyP
          Format = pathAbsoluteOrEmptyF }

    static member Format =
        Formatting.format PathAbsoluteOrEmpty.Mapping.Format

    static member Parse =
        Parsing.parse PathAbsoluteOrEmpty.Mapping.Parse

    static member TryParse =
        Parsing.tryParse PathAbsoluteOrEmpty.Mapping.Parse

    override x.ToString () =
        PathAbsoluteOrEmpty.Format x

(* Absolute *)

type PathAbsolute =
    | PathAbsolute of string list

    static member internal Mapping =

        let pathAbsoluteP =
            skipChar '/' >>. opt (notEmpty pcharParser .>>. many (skipChar '/' >>. pcharParser))
            |>> function | Some (x, xs) -> PathAbsolute (x :: xs)
                         | _ -> PathAbsolute []

        let pathAbsoluteF =
            function | PathAbsolute xs -> append "/" >> join pcharFormatter (append "/") xs

        { Parse = pathAbsoluteP
          Format = pathAbsoluteF }

    static member Format =
        Formatting.format PathAbsolute.Mapping.Format

    static member Parse =
        Parsing.parse PathAbsolute.Mapping.Parse

    static member TryParse =
        Parsing.tryParse PathAbsolute.Mapping.Parse

    override x.ToString () =
        PathAbsolute.Format x

(* No Scheme *)

type PathNoScheme =
    | PathNoScheme of string list

    static member internal Mapping =

        let pathNoSchemeP =
                 notEmpty pcharNcParser 
            .>>. many (skipChar '/' >>. pcharParser)
             |>> fun (x, xs) -> PathNoScheme (x :: xs)

        let pathNoSchemeF =
            function | PathNoScheme xs -> join pcharFormatter (append "/") xs

        { Parse = pathNoSchemeP
          Format = pathNoSchemeF }

    static member Format =
        Formatting.format PathNoScheme.Mapping.Format

    static member Parse =
        Parsing.parse PathNoScheme.Mapping.Parse

    static member TryParse =
        Parsing.tryParse PathNoScheme.Mapping.Parse

    override x.ToString () =
        PathNoScheme.Format x

(* Rootless *)

type PathRootless =
    | PathRootless of string list

    static member internal Mapping =

        let pathRootlessP =
                 notEmpty pcharParser 
            .>>. many (skipChar '/' >>. pcharParser)
             |>> fun (x, xs) -> PathRootless (x :: xs)

        let pathRootlessF =
            function | PathRootless xs -> join pcharFormatter (append "/") xs

        { Parse = pathRootlessP
          Format = pathRootlessF }

    static member Format =
        Formatting.format PathRootless.Mapping.Format

    static member Parse =
        Parsing.parse PathRootless.Mapping.Parse

    static member TryParse =
        Parsing.tryParse PathRootless.Mapping.Parse

    override x.ToString () =
        PathRootless.Format x

(* Query

   Taken from RFC 3986, Section 3.4 Query
   See [http://tools.ietf.org/html/rfc3986#section-3.4] *)

type Query =
    | Query of string

    static member internal Mapping =

        let isQueryChar i =
                isPchar i
             || i = 0x2f // /
             || i = 0x3f // ?

        let parser =
            PercentEncoding.makeParser isQueryChar

        let formatter =
            PercentEncoding.makeFormatter isQueryChar

        let queryP =
            skipChar '?' >>. parser |>> Query

        let queryF =
            function | Query x -> append "?" >> formatter x

        { Parse = queryP
          Format = queryF }

    static member Format =
        Formatting.format Query.Mapping.Format

    static member Parse =
        Parsing.parse Query.Mapping.Parse

    static member TryParse =
        Parsing.tryParse Query.Mapping.Parse

    override x.ToString () =
        Query.Format x

(* Fragment

   Taken from RFC 3986, Section 3.5 Fragment
   See [http://tools.ietf.org/html/rfc3986#section-3.5] *)

type Fragment =
    | Fragment of string

    static member internal Mapping =
    
        let isFragmentChar i =
                isPchar i
             || i = 0x2f // /
             || i = 0x3f // ?

        let parser =
            PercentEncoding.makeParser isFragmentChar

        let formatter =
            PercentEncoding.makeFormatter isFragmentChar

        let fragmentP =
            skipChar '#' >>. parser |>> Fragment

        let fragmentF =
            function | Fragment x -> append "#" >> formatter x

        { Parse = fragmentP
          Format = fragmentF }

    static member Format =
        Formatting.format Fragment.Mapping.Format

    static member Parse =
        Parsing.parse Fragment.Mapping.Parse

    static member TryParse =
        Parsing.tryParse Fragment.Mapping.Parse

    override x.ToString () =
        Fragment.Format x

(* URI

   Taken from RFC 3986, Section 3 URI
   See [http://tools.ietf.org/html/rfc3986#section-3] *)

(* Note: In the case of absolute paths in the hierarchy, which the parser
   will correctly determine, the type system cannot adequately protect
   against an absolute path being created with two initial empty
   segments (and in the absence of a strong dependent system, it's likely
   to stay that way without extreme convolution). (Created in this case
   refers to direct instance creation).

   It is therefore possible to create an invalid URI string using the URI
   type if some care is not taken. For now this will simply have to stand
   as an allowed but "known not ideal" behaviour, under review.
   
   It is of course also possible to create invalid paths by creating strings
   which are invalid segments. Though the parser will reject these, manual
   creation will still allow this case. *)

type Uri =
    | Uri of Scheme * HierarchyPart * Query option * Fragment option

    static member internal Mapping =

        let uriP =
                 Scheme.Mapping.Parse .>> skipChar ':'
            .>>. HierarchyPart.Mapping.Parse 
            .>>. opt Query.Mapping.Parse
            .>>. opt Fragment.Mapping.Parse
             |>> fun (((scheme, hierarchy), query), fragment) ->
                Uri (scheme, hierarchy, query, fragment)

        let uriF =
            function | Uri (s, h, q, f) -> 
                        let formatters =
                            [ Scheme.Mapping.Format s
                              append ":"
                              HierarchyPart.Mapping.Format h
                              (function | Some q -> Query.Mapping.Format q 
                                        | _ -> id) q
                              (function | Some f -> Fragment.Mapping.Format f 
                                        | _ -> id) f ]

                        fun b -> List.fold (|>) b formatters

        { Parse = uriP
          Format = uriF }

    static member Format =
        Formatting.format Uri.Mapping.Format

    static member Parse =
        Parsing.parse Uri.Mapping.Parse

    static member TryParse =
        Parsing.tryParse Uri.Mapping.Parse

    override x.ToString () =
        Uri.Format x

and HierarchyPart =
    | Authority of Authority * PathAbsoluteOrEmpty
    | Absolute of PathAbsolute
    | Rootless of PathRootless
    | Empty

    static member internal Mapping =

        let authorityP =
                 skipString "//" >>. Authority.Mapping.Parse 
            .>>. PathAbsoluteOrEmpty.Mapping.Parse 
             |>> Authority

        let hierarchyPartP =
            choice [
                authorityP
                PathAbsolute.Mapping.Parse |>> Absolute
                PathRootless.Mapping.Parse |>> Rootless
                preturn Empty ]

        let authorityF (a, p)=
                append "//" 
             >> Authority.Mapping.Format a 
             >> PathAbsoluteOrEmpty.Mapping.Format p

        let hierarchyPartF =
            function | Authority (a, p) -> authorityF (a, p)
                     | Absolute p -> PathAbsolute.Mapping.Format p
                     | Rootless p -> PathRootless.Mapping.Format p
                     | Empty -> id

        { Parse = hierarchyPartP
          Format = hierarchyPartF }

(* Relative Reference

   Taken from RFC 3986, Section 4.2 Relative Reference
   See [http://tools.ietf.org/html/rfc3986#section-4.2] *)

type RelativeReference =
    | RelativeReference of RelativePart * Query option * Fragment option

    static member internal Mapping =

        let relativeReferenceP =
                 RelativePart.Mapping.Parse
            .>>. opt Query.Mapping.Parse
            .>>. opt Fragment.Mapping.Parse
             |>> fun ((relative, query), fragment) ->
                RelativeReference (relative, query, fragment)

        let relativeReferenceF =
            function | RelativeReference (r, q, f) -> 
                        let formatters =
                            [ RelativePart.Mapping.Format r
                              (function | Some q -> Query.Mapping.Format q
                                        | _ -> id) q
                              (function | Some f -> Fragment.Mapping.Format f
                                        | _ -> id) f ]

                        fun b -> List.fold (|>) b formatters

        { Parse = relativeReferenceP
          Format = relativeReferenceF }

    static member Format =
        Formatting.format RelativeReference.Mapping.Format

    static member Parse =
        Parsing.parse RelativeReference.Mapping.Parse

    static member TryParse =
        Parsing.tryParse RelativeReference.Mapping.Parse

    override x.ToString () =
        RelativeReference.Format x

and RelativePart =
    | Authority of Authority * PathAbsoluteOrEmpty
    | Absolute of PathAbsolute
    | NoScheme of PathNoScheme
    | Empty

    static member internal Mapping =

        let authorityP =
                 skipString "//" >>. Authority.Mapping.Parse
            .>>. PathAbsoluteOrEmpty.Mapping.Parse 
             |>> Authority

        let relativePartP =
            choice [
                authorityP
                PathAbsolute.Mapping.Parse |>> Absolute
                PathNoScheme.Mapping.Parse |>> NoScheme
                preturn Empty ]

        let authorityF (a, p) =
                append "//" 
             >> Authority.Mapping.Format a 
             >> PathAbsoluteOrEmpty.Mapping.Format p

        let relativePartF =
            function | Authority (a, p) -> authorityF (a, p)
                     | Absolute p -> PathAbsolute.Mapping.Format p
                     | NoScheme p -> PathNoScheme.Mapping.Format p
                     | Empty -> id

        { Parse = relativePartP
          Format = relativePartF }

(* Absolute URI

   Taken from RFC 3986, Section 4.3 Absolute URI
   See [http://tools.ietf.org/html/rfc3986#section-4.3] *)

type AbsoluteUri =
    | AbsoluteUri of Scheme * HierarchyPart * Query option

    static member internal Mapping =

        let absoluteUriP =
                 Scheme.Mapping.Parse .>> skipChar ':' 
            .>>. HierarchyPart.Mapping.Parse 
            .>>. opt Query.Mapping.Parse
             |>> fun ((scheme, hierarchy), query) ->
                AbsoluteUri (scheme, hierarchy, query)

        let absoluteUriF =
            function | AbsoluteUri (s, h, q) -> 
                        let formatters =
                            [ Scheme.Mapping.Format s
                              append ":"
                              HierarchyPart.Mapping.Format h
                              (function | Some q -> Query.Mapping.Format q
                                        | _ -> id) q ]

                        fun b -> List.fold (|>) b formatters

        { Parse = absoluteUriP
          Format = absoluteUriF }

    static member Format =
        Formatting.format AbsoluteUri.Mapping.Format

    static member Parse =
        Parsing.parse AbsoluteUri.Mapping.Parse

    static member TryParse =
        Parsing.tryParse AbsoluteUri.Mapping.Parse

    override x.ToString () =
        AbsoluteUri.Format x

(* URI Reference

   Taken from RFC 3986, Section 4.1 URI Reference
   See [http://tools.ietf.org/html/rfc3986#section-4.1] *)

type UriReference =
    | Uri of Uri
    | Relative of RelativeReference

    static member internal Mapping =

        let uriReferenceP =
            choice [
                attempt Uri.Mapping.Parse |>> Uri
                RelativeReference.Mapping.Parse |>> Relative ]

        let uriReferenceF =
            function | Uri x -> Uri.Mapping.Format x
                     | Relative x -> RelativeReference.Mapping.Format x

        { Parse = uriReferenceP
          Format = uriReferenceF }

    static member Format =
        Formatting.format UriReference.Mapping.Format

    static member Parse =
        Parsing.parse UriReference.Mapping.Parse

    static member TryParse =
        Parsing.tryParse UriReference.Mapping.Parse

    override x.ToString () =
        UriReference.Format x