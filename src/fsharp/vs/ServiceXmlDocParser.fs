// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.FSharp.Compiler.SourceCodeServices

[<RequireQualifiedAccess>]
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module Array =
    /// pass an array byref to reverse it in place
    let revInPlace (array: 'T []) =
        if Array.isEmpty array then () else
        let arrlen, revlen = array.Length-1, array.Length/2 - 1
        for idx in 0 .. revlen do
            let t1 = array.[idx] 
            let t2 = array.[arrlen-idx]
            array.[idx] <- t2
            array.[arrlen-idx] <- t1

    /// Async implementation of Array.map.
    let mapAsync (mapping : 'T -> Async<'U>) (array : 'T[]) : Async<'U[]> =
        let len = Array.length array
        let result = Array.zeroCreate len

        async { // Apply the mapping function to each array element.
            for i in 0 .. len - 1 do
                let! mappedValue = mapping array.[i]
                result.[i] <- mappedValue

            // Return the completed results.
            return result
        }

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module String =
    open System
    open System.IO

    let inline toCharArray (str: string) = str.ToCharArray()

    let lowerCaseFirstChar (str: string) =
        if String.IsNullOrEmpty str 
         || Char.IsLower(str, 0) then str else 
        let strArr = toCharArray str
        match Array.tryHead strArr with
        | None -> str
        | Some c  -> 
            strArr.[0] <- Char.ToLower c
            String (strArr)

    let extractTrailingIndex (str: string) =
        match str with
        | null -> null, None
        | _ ->
            let charr = str.ToCharArray() 
            Array.revInPlace charr
            let digits = Array.takeWhile Char.IsDigit charr
            Array.revInPlace digits
            String digits
            |> function
               | "" -> str, None
               | index -> str.Substring (0, str.Length - index.Length), Some (int index)

    /// Remove all trailing and leading whitespace from the string
    /// return null if the string is null
    let trim (value: string) = if isNull value then null else value.Trim()
    
    /// Splits a string into substrings based on the strings in the array separators
    let split options (separator: string []) (value: string) = 
        if isNull value  then null else value.Split(separator, options)

    let (|StartsWith|_|) pattern value =
        if String.IsNullOrWhiteSpace value then
            None
        elif value.StartsWith pattern then
            Some()
        else None

    let (|Contains|_|) pattern value =
        if String.IsNullOrWhiteSpace value then
            None
        elif value.Contains pattern then
            Some()
        else None

    let getLines (str: string) =
        use reader = new StringReader(str)
        [|
        let line = ref (reader.ReadLine())
        while not (isNull !line) do
            yield !line
            line := reader.ReadLine()
        if str.EndsWith("\n") then
            // last trailing space not returned
            // http://stackoverflow.com/questions/19365404/stringreader-omits-trailing-linebreak
            yield String.Empty
        |]

/// Represent an Xml documentation block in source code
type XmlDocable =
    | XmlDocable of line:int * indent:int * paramNames:string list

module internal XmlDocParsing =
    open Microsoft.FSharp.Compiler.Range
    open Microsoft.FSharp.Compiler.Ast
        
    let (|ConstructorPats|) = function
        | Pats ps -> ps
        | NamePatPairs(xs, _) -> List.map snd xs

    let rec digNamesFrom = function
        | SynPat.Named(_innerPat,id,_isTheThisVar,_access,_range) -> [id.idText]
        | SynPat.Typed(pat,_type,_range) -> digNamesFrom pat
        | SynPat.Attrib(pat,_attrs,_range) -> digNamesFrom pat
        | SynPat.LongIdent(_lid,_idOpt,_typDeclsOpt,ConstructorPats pats,_access,_range) -> 
            pats |> List.collect digNamesFrom 
        | SynPat.Tuple(pats,_range)
        | SynPat.StructTuple(pats,_range) -> pats |> List.collect digNamesFrom 
        | SynPat.Paren(pat,_range) -> digNamesFrom pat
        | SynPat.OptionalVal (id, _) -> [id.idText]
        | SynPat.Or _           // no one uses ors in fun decls
        | SynPat.Ands _         // no one uses ands in fun decls
        | SynPat.ArrayOrList _  // no one uses this in fun decls
        | SynPat.Record _       // no one uses this in fun decls
        | SynPat.Null _
        | SynPat.Const _
        | SynPat.Wild _
        | SynPat.IsInst _
        | SynPat.QuoteExpr _
        | SynPat.DeprecatedCharRange _
        | SynPat.InstanceMember _
        | SynPat.FromParseError _ -> []

    let getXmlDocablesImpl(sourceCodeLinesOfTheFile: string [], input: ParsedInput option) =
        let indentOf (lineNum: int) =
            let mutable i = 0
            let line = sourceCodeLinesOfTheFile.[lineNum-1] // -1 because lineNum reported by xmldocs are 1-based, but array is 0-based
            while i < line.Length && line.Chars(i) = ' ' do
                i <- i + 1
            i

        let isEmptyXmlDoc (preXmlDoc: PreXmlDoc) =
            match preXmlDoc.ToXmlDoc() with 
            | XmlDoc [||] -> true
            | XmlDoc [|x|] when x.Trim() = "" -> true
            | _ -> false

        let rec getXmlDocablesSynModuleDecl = function
            | SynModuleDecl.NestedModule(_,  _, synModuleDecls, _, _) -> 
                (synModuleDecls |> List.collect getXmlDocablesSynModuleDecl)
            | SynModuleDecl.Let(_, synBindingList, range) -> 
                let anyXmlDoc = 
                    synBindingList |> List.exists (fun (SynBinding.Binding(_, _, _, _, _, preXmlDoc, _, _, _, _, _, _)) -> 
                        not <| isEmptyXmlDoc preXmlDoc)
                if anyXmlDoc then [] else
                let synAttributes = 
                    synBindingList |> List.collect (fun (SynBinding.Binding(_, _, _, _, a, _, _, _, _, _, _, _)) -> a)
                let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                let line = fullRange.StartLine 
                let indent = indentOf line
                [ for SynBinding.Binding(_, _, _, _, _, _, synValData, synPat, _, _, _, _) in synBindingList do
                      match synValData with
                      | SynValData(_memberFlagsOpt, SynValInfo(args, _), _) when not (List.isEmpty args) -> 
                          let parameters =
                              args 
                              |> List.collect (
                                    List.collect (fun (SynArgInfo(_, _, ident)) -> 
                                        match ident with 
                                        | Some ident -> [ident.idText]
                                        | None -> []))
                          match parameters with
                          | [] ->
                              let paramNames = digNamesFrom synPat
                              yield! paramNames
                          | _ :: _ ->
                             yield! parameters
                      | _ -> () ]
                |> fun paramNames -> [ XmlDocable(line,indent,paramNames) ]
            | SynModuleDecl.Types(synTypeDefnList, _) -> (synTypeDefnList |> List.collect getXmlDocablesSynTypeDefn)
            | SynModuleDecl.NamespaceFragment(synModuleOrNamespace) -> getXmlDocablesSynModuleOrNamespace synModuleOrNamespace
            | SynModuleDecl.ModuleAbbrev _
            | SynModuleDecl.DoExpr _
            | SynModuleDecl.Exception _
            | SynModuleDecl.Open _
            | SynModuleDecl.Attributes _
            | SynModuleDecl.HashDirective _ -> []

        and getXmlDocablesSynModuleOrNamespace (SynModuleOrNamespace(_, _,  _, synModuleDecls, _, _, _, _)) =
            (synModuleDecls |> List.collect getXmlDocablesSynModuleDecl)

        and getXmlDocablesSynTypeDefn (SynTypeDefn.TypeDefn(ComponentInfo(synAttributes, _, _, _, preXmlDoc, _, _, compRange), synTypeDefnRepr, synMemberDefns, tRange)) =
            let stuff = 
                match synTypeDefnRepr with
                | SynTypeDefnRepr.ObjectModel(_, synMemberDefns, _) -> (synMemberDefns |> List.collect getXmlDocablesSynMemberDefn)
                | SynTypeDefnRepr.Simple(_synTypeDefnSimpleRepr, _range) -> []
                | SynTypeDefnRepr.Exception _ -> []
            let docForTypeDefn = 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) (unionRanges compRange tRange)
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    [XmlDocable(line,indent,[])]
                else []
            docForTypeDefn @ stuff @ (synMemberDefns |> List.collect getXmlDocablesSynMemberDefn)

        and getXmlDocablesSynMemberDefn = function
            | SynMemberDefn.Member(SynBinding.Binding(_, _, _, _, synAttributes, preXmlDoc, _, synPat, _, _, _, _), memRange) -> 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) memRange
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    let paramNames = digNamesFrom synPat 
                    [XmlDocable(line,indent,paramNames)]
                else []
            | SynMemberDefn.AbstractSlot(ValSpfn(synAttributes, _, _, _, SynValInfo(args, _), _, _, preXmlDoc, _, _, _), _, range) -> 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    let paramNames = args |> List.collect (fun az -> az |> List.choose (fun (SynArgInfo(_synAttributes, _, idOpt)) -> match idOpt with | Some id -> Some(id.idText) | _ -> None))
                    [XmlDocable(line,indent,paramNames)]
                else []
            | SynMemberDefn.Interface(_synType, synMemberDefnsOption, _range) -> 
                match synMemberDefnsOption with 
                | None -> [] 
                | Some(x) -> x |> List.collect getXmlDocablesSynMemberDefn
            | SynMemberDefn.NestedType(synTypeDefn, _, _) -> getXmlDocablesSynTypeDefn synTypeDefn
            | SynMemberDefn.AutoProperty(synAttributes, _, _, _, _, _, _, _, _, _, range) -> 
                let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                let line = fullRange.StartLine 
                let indent = indentOf line
                [XmlDocable(line, indent, [])]
            | SynMemberDefn.Open _
            | SynMemberDefn.ImplicitCtor _
            | SynMemberDefn.ImplicitInherit _
            | SynMemberDefn.Inherit _
            | SynMemberDefn.ValField _
            | SynMemberDefn.LetBindings _ -> []

        and getXmlDocablesInput input =
            match input with
            | ParsedInput.ImplFile(ParsedImplFileInput(_, _, _, _, _, symModules, _))-> 
                symModules |> List.collect getXmlDocablesSynModuleOrNamespace
            | ParsedInput.SigFile _ -> []

        async {
            // Get compiler options for the 'project' implied by a single script file
            match input with
            | Some input -> 
                return getXmlDocablesInput input
            | None ->
                // Should not fail here, just in case 
                return []
        }

module internal XmlDocComment =
    let private ws (s: string, pos) = 
        let res = s.TrimStart()
        Some (res, pos + (s.Length - res.Length))

    let private str (prefix: string) (s: string, pos) =
        match s.StartsWith prefix with
        | true -> 
            let res = s.Substring prefix.Length
            Some (res, pos + (s.Length - res.Length))
        | _ -> None

    let private eol (s: string, pos) = 
        match s with
        | "" -> Some ("", pos)
        | _ -> None

    let inline private (>=>) f g = f >> Option.bind g
    
    // if it's a blank XML comment with trailing "<", returns Some (index of the "<"), otherwise returns None
    let isBlank (s: string) =
        let parser = ws >=> str "///" >=> ws >=> str "<" >=> eol
        let res = parser (s.TrimEnd(), 0) |> Option.map snd |> Option.map (fun x -> x - 1)
        res

module internal XmlDocParser =
    /// Get the list of Xml documentation from current source code
    let getXmlDocables (sourceCodeOfTheFile, input) =
        let sourceCodeLinesOfTheFile = String.getLines sourceCodeOfTheFile
        XmlDocParsing.getXmlDocablesImpl (sourceCodeLinesOfTheFile, input)