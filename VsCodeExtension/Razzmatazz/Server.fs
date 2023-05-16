module CSharpLanguageServer.Server

open System
open System.Text
open System.IO
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.Completion
open Microsoft.CodeAnalysis.Rename
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.CodeFixes
open Microsoft.CodeAnalysis.Classification
open Microsoft.Build.Locator
open Newtonsoft.Json.Linq
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types
open RoslynHelpers
open State
open System.Threading.Tasks
open Util

type ServerSettingsDto = {
     csharp: ServerSettingsCSharpDto option
}
and ServerSettingsCSharpDto =
  { solution: string option }
  static member Default = { solution = None }

type CSharpMetadataParams = {
    TextDocument: TextDocumentIdentifier
}

type CSharpMetadataResponse = CSharpMetadata

type CSharpCodeActionResolutionData = {
    TextDocumentUri: string
    Range: Range
}

type CodeLensData = { DocumentUri: string; Position: Position  }

let emptyCodeLensData = { DocumentUri=""; Position={ Line=0; Character=0 } }

type InvocationContext = {
    Receiver: SyntaxNode
    ArgumentTypes: TypeInfo list
    Separators: SyntaxToken list
}

type CSharpLspClient(sendServerNotification: ClientNotificationSender, sendServerRequest: ClientRequestSender) =
    inherit LspClient ()

    override __.WindowShowMessage(p) =
        sendServerNotification "window/showMessage" (box p) |> Async.Ignore

    override __.WindowShowMessageRequest(p) =
        sendServerRequest.Send "window/showMessageRequest" (box p)

    override __.WindowLogMessage(p) =
        sendServerNotification "window/logMessage" (box p) |> Async.Ignore

    override __.TelemetryEvent(p) =
        sendServerNotification "telemetry/event" (box p) |> Async.Ignore

    override __.ClientRegisterCapability(p) =
        sendServerRequest.Send "client/registerCapability" (box p)

    override __.ClientUnregisterCapability(p) =
        sendServerRequest.Send "client/unregisterCapability" (box p)

    override __.WorkspaceWorkspaceFolders () =
        sendServerRequest.Send "workspace/workspaceFolders" ()

    override __.WorkspaceConfiguration (p) =
        sendServerRequest.Send "workspace/configuration" (box p)

    override __.WorkspaceApplyEdit (p) =
        sendServerRequest.Send "workspace/applyEdit" (box p)

    override __.WorkspaceSemanticTokensRefresh () =
        sendServerNotification "workspace/semanticTokens/refresh" () |> Async.Ignore

    override __.TextDocumentPublishDiagnostics(p) =
        sendServerNotification "textDocument/publishDiagnostics" (box p) |> Async.Ignore

type Data<'TParameters> = {
    Scope: ServerRequestScope
    Parameters: 'TParameters
    LspClient: LspClient
    StateActor: MailboxProcessor<ServerStateEvent>
    Diagnostics: MailboxProcessor<DiagnosticsEvent>
    LogInfo: AsyncLogFn
    LogMessage: AsyncLogFn
}

let success = LspResult.success

let getDotnetCliVersion () : string =
    use proc = new Process()
    proc.StartInfo <- ProcessStartInfo()
    proc.StartInfo.FileName <- "dotnet"
    proc.StartInfo.Arguments <- "--version"
    proc.StartInfo.UseShellExecute <- false
    proc.StartInfo.RedirectStandardOutput <- true
    proc.StartInfo.CreateNoWindow <- true

    let startOK = proc.Start()
    if startOK then
        let sbuilder = StringBuilder()
        while (not (proc.StandardOutput.EndOfStream)) do
            sbuilder.Append(proc.StandardOutput.ReadLine()) |> ignore

        sbuilder.ToString()
    else
        "(could not launch `dotnet --version`)"

let getDocumentForUriFromCurrentState docType uri (stateActor: MailboxProcessor<ServerStateEvent>) =
    stateActor.PostAndAsyncReply(fun rc -> GetDocumentOfTypeForUri (docType, uri, rc))

let setupTimer(diagnostics: MailboxProcessor<DiagnosticsEvent>, stateActor: MailboxProcessor<ServerStateEvent>) =
    let callback = System.Threading.TimerCallback(
        fun _ -> do diagnostics.Post(ProcessPendingDiagnostics); do stateActor.Post(PeriodicTimerTick))
    new System.Threading.Timer(
        callback,
        null,
        dueTime=1000,
        period=250)

let logMessageWithLevel(l, message, lspClient: LspClient) = async {
    let messageParams = { Type = l ; Message = "csharp-ls: " + message }
    do! lspClient.WindowShowMessage messageParams
}

let handleInitialize(data: Data<InitializeParams>): AsyncLspResult<InitializeResult> = async {
    do! data.LogInfo(
        sprintf "initializing, csharp-ls version %s; cwd: \"%s\""
            (typeof<CSharpLspClient>.Assembly.GetName().Version |> string)
            (Directory.GetCurrentDirectory()))

    do! data.LogInfo(
        "csharp-ls is released under MIT license and is not affiliated with Microsoft Corp.; see https://github.com/razzmatazz/csharp-language-server")

    let vsInstanceQueryOpt = VisualStudioInstanceQueryOptions.Default
    let vsInstanceList = MSBuildLocator.QueryVisualStudioInstances(vsInstanceQueryOpt)
    if Seq.isEmpty vsInstanceList then
        raise (InvalidOperationException("No instances of MSBuild could be detected." + Environment.NewLine + "Try calling RegisterInstance or RegisterMSBuildPath to manually register one."))

    let vsInstance = vsInstanceList |> Seq.head

    do! data.LogInfo(
        sprintf "MSBuildLocator: will register \"%s\", Version=%s as default instance"
            vsInstance.Name
            (string vsInstance.Version))

    MSBuildLocator.RegisterInstance(vsInstance)

    data.Scope.Emit(ClientCapabilityChange data.Parameters.Capabilities)

    let clientSupportsRenameOptions =
        data.Parameters.Capabilities
        |> Option.bind (fun x -> x.TextDocument)
        |> Option.bind (fun x -> x.Rename)
        |> Option.bind (fun x -> x.PrepareSupport)
        |> Option.defaultValue false

    let initializeResult = {
        InitializeResult.Default with
            Capabilities =
                { ServerCapabilities.Default with
                    HoverProvider = Some true
                    RenameProvider =
                        if clientSupportsRenameOptions then
                            Second { PrepareProvider = Some true } |> Some
                        else
                            true |> First |> Some
                    DefinitionProvider = Some true
                    TypeDefinitionProvider = None
                    ImplementationProvider = Some true
                    ReferencesProvider = Some true
                    DocumentHighlightProvider = Some true
                    DocumentSymbolProvider = Some true
                    WorkspaceSymbolProvider = Some true
                    DocumentFormattingProvider = Some true
                    DocumentRangeFormattingProvider = Some true
                    DocumentOnTypeFormattingProvider = Some {
                        FirstTriggerCharacter = ';'
                        MoreTriggerCharacter = Some([| '}'; ')' |]) }
                    SignatureHelpProvider = Some {
                        TriggerCharacters = Some([| '('; ','; '<'; '{'; '[' |])
                        RetriggerCharacters = None }
                    CompletionProvider = Some {
                        ResolveProvider = None
                        TriggerCharacters = Some ([| '.'; '''; |])
                        AllCommitCharacters = None }
                    CodeLensProvider = Some {
                        ResolveProvider = Some true }
                    CodeActionProvider = Some {
                        CodeActionKinds = None
                        ResolveProvider = Some true }
                    TextDocumentSync = Some {
                        TextDocumentSyncOptions.Default with
                            OpenClose = Some true
                            Save = Some { IncludeText = Some true }
                            Change = Some TextDocumentSyncKind.Incremental }
                    FoldingRangeProvider = None
                    SelectionRangeProvider = None
                    SemanticTokensProvider = Some {
                        Legend = {
                            TokenTypes = SemanticTokenTypes |> Seq.toArray
                            TokenModifiers = SemanticTokenModifiers |> Seq.toArray }
                        Range = Some true
                        Full = true |> First |> Some }
                    InlayHintProvider = Some { ResolveProvider = Some false }
                    TypeHierarchyProvider = Some true
                    CallHierarchyProvider = Some true
                }
            }

    return initializeResult |> success
}

let handleInitialized(data: Data<InitializedParams>): Async<LspResult<unit>> = async {
    let clientSupportsWorkspaceDidChangeWatchedFilesDynamicReg =
        data.Scope.ClientCapabilities
        |> Option.bind (fun x -> x.Workspace)
        |> Option.bind (fun x -> x.DidChangeWatchedFiles)
        |> Option.bind (fun x -> x.DynamicRegistration)
        |> Option.defaultValue true

    match clientSupportsWorkspaceDidChangeWatchedFilesDynamicReg with
    | true ->
        let fileChangeWatcher = {
            GlobPattern = "**/*.{cs,csproj,sln}"
            Kind = None }

        let didChangeWatchedFilesRegistration: Types.Registration =
            {
                Id = "id:workspace/didChangeWatchedFiles"
                Method = "workspace/didChangeWatchedFiles"
                RegisterOptions = { Watchers = [| fileChangeWatcher |] } |> serialize |> Some
            }

        try
            let! regResult = data.LspClient.ClientRegisterCapability(
                { Registrations = [| didChangeWatchedFilesRegistration |] })

            match regResult with
            | Ok _ -> ()
            | Error error ->
                do! data.LogInfo(
                    sprintf "handleInitialized: didChangeWatchedFiles registration has failed with %s"
                        (error |> string))
        with
        | ex ->
            do! data.LogInfo(
                sprintf "handleInitialized: didChangeWatchedFiles registration has failed with %s"
                    (ex |> string))
    | false -> ()

    //
    // retrieve csharp settings
    //
    try
        let! workspaceCSharpConfig =
            data.LspClient.WorkspaceConfiguration(
                { items=[| { Section=Some "csharp"; ScopeUri=None } |] })

        let csharpConfigTokensMaybe =
            match workspaceCSharpConfig with
            | Ok ts -> Some ts
            | _ -> None

        let newSettingsMaybe =
            match csharpConfigTokensMaybe with
            | Some [| t |] ->
                let csharpSettingsMaybe = t |> deserialize<ServerSettingsCSharpDto option>

                match csharpSettingsMaybe with
                | Some csharpSettings ->
                    Some { data.Scope.State.Settings with SolutionPath = csharpSettings.solution }
                | _ -> None
            | _ -> None

        match newSettingsMaybe with
        | Some newSettings ->
            data.Scope.Emit(SettingsChange newSettings)
        | _ -> ()
    with
    | ex ->
        do! data.LogInfo(
            sprintf "handleInitialized: could not retrieve `csharp` workspace configuration section: %s"
                (ex |> string))

    //
    // start loading the solution
    //
    data.StateActor.Post(SolutionReloadRequest (TimeSpan.FromMilliseconds(100)))

    return LspResult.Ok()
}

let handleTextDocumentDidOpen(data: Data<Types.DidOpenTextDocumentParams>): Async<LspResult<unit>> =
    match data.Scope.GetDocumentForUriOfType AnyDocument data.Parameters.TextDocument.Uri with
    | Some (doc, docType) ->
        match docType with
        | UserDocument ->
            // we want to load the document in case it has been changed since we have the solution loaded
            // also, as a bonus we can recover from corrupted document view in case document in roslyn solution
            // went out of sync with editor
            let updatedDoc = SourceText.From(data.Parameters.TextDocument.Text) |> doc.WithText

            data.Scope.Emit(OpenDocVersionAdd (data.Parameters.TextDocument.Uri, data.Parameters.TextDocument.Version))
            data.Scope.Emit(SolutionChange updatedDoc.Project.Solution)

            data.Diagnostics.Post(
                DocumentOpenOrChange (data.Parameters.TextDocument.Uri, DateTime.Now))

            LspResult.Ok() |> async.Return

        | _ ->
            LspResult.Ok() |> async.Return

    | None ->
        let docFilePathMaybe = Util.tryParseFileUri data.Parameters.TextDocument.Uri

        match docFilePathMaybe with
        | Some docFilePath -> async {
            // ok, this document is not on solution, register a new document
            let! newDocMaybe =
                tryAddDocument
                    data.LogMessage
                    docFilePath
                    data.Parameters.TextDocument.Text
                    data.Scope.Solution

            match newDocMaybe with
            | Some newDoc ->
                data.Scope.Emit(SolutionChange newDoc.Project.Solution)

                data.Diagnostics.Post(
                    DocumentOpenOrChange (data.Parameters.TextDocument.Uri, DateTime.Now))

            | None -> ()

            return LspResult.Ok() }

        | None ->
            LspResult.Ok() |> async.Return

let handleTextDocumentDidChange(data: Data<Types.DidChangeTextDocumentParams>): Async<LspResult<unit>> =
    async {
        let docMaybe = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
        match docMaybe with
        | Some doc ->
            let! ct = Async.CancellationToken
            let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask

            let updatedSourceText = sourceText |> applyLspContentChangesOnRoslynSourceText data.Parameters.ContentChanges
            let updatedDoc = doc.WithText(updatedSourceText)

            let updatedSolution = updatedDoc.Project.Solution

            data.Scope.Emit(SolutionChange updatedSolution)
            data.Scope.Emit(OpenDocVersionAdd (data.Parameters.TextDocument.Uri, data.Parameters.TextDocument.Version |> Option.defaultValue 0))

            data.Diagnostics.Post(
                DocumentOpenOrChange (data.Parameters.TextDocument.Uri, DateTime.Now))

        | None -> ()

        return Result.Ok()
    }

let handleTextDocumentDidSave(data: Data<Types.DidSaveTextDocumentParams>): Async<LspResult<unit>> =
    // we need to add this file to solution if not already
    let doc = data.Scope.GetAnyDocumentForUri data.Parameters.TextDocument.Uri

    match doc with
    | Some _ ->
        LspResult.Ok() |> async.Return

    | None -> async {
        let docFilePath = Util.parseFileUri data.Parameters.TextDocument.Uri
        let! newDocMaybe =
            tryAddDocument
                data.LogMessage
                docFilePath
                data.Parameters.Text.Value
                data.Scope.Solution

        match newDocMaybe with
        | Some newDoc ->
            data.Scope.Emit(SolutionChange newDoc.Project.Solution)

            data.Diagnostics.Post(
                DocumentOpenOrChange (data.Parameters.TextDocument.Uri, DateTime.Now))

        | None -> ()

        return LspResult.Ok()
    }

let handleTextDocumentDidClose(data: Data<Types.DidCloseTextDocumentParams>): Async<LspResult<unit>> =
    data.Scope.Emit(OpenDocVersionRemove data.Parameters.TextDocument.Uri)
    data.Diagnostics.Post(DocumentClose data.Parameters.TextDocument.Uri)
    LspResult.Ok() |> async.Return

let handleTextDocumentCodeAction(data: Data<Types.CodeActionParams>): AsyncLspResult<Types.TextDocumentCodeActionResult option> = async {
    let docMaybe = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    match docMaybe with
    | None ->
        return None |> success

    | Some doc ->
        let! ct = Async.CancellationToken
        let! docText = doc.GetTextAsync(ct) |> Async.AwaitTask

        let textSpan =
            data.Parameters.Range
            |> roslynLinePositionSpanForLspRange docText.Lines
            |> docText.Lines.GetTextSpan

        let! roslynCodeActions =
            getRoslynCodeActions data.LogMessage doc textSpan

        let clientSupportsCodeActionEditResolveWithEditAndData =
            data.Scope.ClientCapabilities
            |> Option.bind (fun x -> x.TextDocument)
            |> Option.bind (fun x -> x.CodeAction)
            |> Option.bind (fun x -> x.ResolveSupport)
            |> Option.map (fun resolveSupport -> resolveSupport.Properties |> Array.contains "edit")
            |> Option.defaultValue false

        let! lspCodeActions =
            match clientSupportsCodeActionEditResolveWithEditAndData with
            | true -> async {
                let toUnresolvedLspCodeAction (ca: Microsoft.CodeAnalysis.CodeActions.CodeAction) =
                    let resolutionData: CSharpCodeActionResolutionData = {
                        TextDocumentUri = data.Parameters.TextDocument.Uri
                        Range = data.Parameters.Range }

                    let lspCa = roslynCodeActionToUnresolvedLspCodeAction ca
                    { lspCa with Data = resolutionData |> serialize |> Some }

                return roslynCodeActions |> Seq.map toUnresolvedLspCodeAction |> Array.ofSeq }
            | false -> async {
                let results = List<CodeAction>()

                for ca in roslynCodeActions do
                    let! maybeLspCa =
                        roslynCodeActionToResolvedLspCodeAction
                            data.Scope.Solution
                            data.Scope.OpenDocVersions.TryFind
                            data.LogMessage
                            doc
                            ca

                    if maybeLspCa.IsSome then
                        results.Add(maybeLspCa.Value)

                return results |> Array.ofSeq }

        return
            lspCodeActions
            |> Seq.sortByDescending (fun ca -> ca.IsPreferred)
            |> Seq.map U2<Command, CodeAction>.Second
            |> Array.ofSeq
            |> Some
            |> success
}

let handleCodeActionResolve(data: Data<CodeAction>): AsyncLspResult<CodeAction option> = async {
    let resolutionData =
        data.Parameters.Data
        |> Option.map deserialize<CSharpCodeActionResolutionData>

    let docMaybe = data.Scope.GetUserDocumentForUri resolutionData.Value.TextDocumentUri

    match docMaybe with
    | None ->
        return None |> success

    | Some doc ->
        let! ct = Async.CancellationToken
        let! docText = doc.GetTextAsync(ct) |> Async.AwaitTask

        let textSpan =
            resolutionData.Value.Range
            |> roslynLinePositionSpanForLspRange docText.Lines
            |> docText.Lines.GetTextSpan

        let! roslynCodeActions =
            getRoslynCodeActions data.LogMessage doc textSpan

        let selectedCodeAction = roslynCodeActions |> Seq.tryFind (fun ca -> ca.Title = data.Parameters.Title)

        let toResolvedLspCodeAction =
            roslynCodeActionToResolvedLspCodeAction
                data.Scope.Solution
                data.Scope.OpenDocVersions.TryFind
                data.LogMessage
                doc

        let! maybeLspCodeAction =
            match selectedCodeAction with
            | Some ca -> async {
                let! resolvedCA = toResolvedLspCodeAction ca

                if resolvedCA.IsNone then
                    do! data.LogMessage (sprintf "handleCodeActionResolve: could not resolve %s - null" (string ca))

                return resolvedCA }
            | None -> async { return None }

        return maybeLspCodeAction |> success
}

let handleTextDocumentCodeLens(data: Data<CodeLensParams>): AsyncLspResult<CodeLens[] option> = async {
    let docMaybe = data.Scope.GetAnyDocumentForUri data.Parameters.TextDocument.Uri
    match docMaybe with
    | Some doc ->
        let! ct = Async.CancellationToken
        let! semanticModel = doc.GetSemanticModelAsync(ct) |> Async.AwaitTask
        let! syntaxTree = doc.GetSyntaxTreeAsync(ct) |> Async.AwaitTask
        let! docText = doc.GetTextAsync(ct) |> Async.AwaitTask

        let collector = DocumentSymbolCollectorForCodeLens(semanticModel)
        collector.Visit(syntaxTree.GetRoot())

        let makeCodeLens (_symbol: ISymbol, nameSpan: TextSpan): CodeLens =
            let start = nameSpan.Start |> docText.Lines.GetLinePosition

            let lensData: CodeLensData = {
                DocumentUri = data.Parameters.TextDocument.Uri
                Position = start |> lspPositionForRoslynLinePosition
            }

            {
                Range = nameSpan |> docText.Lines.GetLinePositionSpan |> lspRangeForRoslynLinePosSpan
                Command = None
                Data = lensData |> JToken.FromObject |> Some
            }

        let codeLens = collector.GetSymbols() |> Seq.map makeCodeLens

        return codeLens |> Array.ofSeq |> Some |> success

    | None ->
        return None |> success
}

let handleCodeLensResolve(data: Data<CodeLens>): AsyncLspResult<CodeLens> = async {
    let! ct = Async.CancellationToken
    let lensData =
        data.Parameters.Data
        |> Option.map (fun t -> t.ToObject<CodeLensData>())
        |> Option.defaultValue emptyCodeLensData

    let docMaybe = data.Scope.GetAnyDocumentForUri lensData.DocumentUri
    let doc = docMaybe.Value

    let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask

    let position =
        LinePosition(lensData.Position.Line, lensData.Position.Character)
        |> sourceText.Lines.GetPosition

    let! symbol = SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct) |> Async.AwaitTask

    let! refs = SymbolFinder.FindReferencesAsync(symbol, doc.Project.Solution, ct) |> Async.AwaitTask
    let locations = refs |> Seq.collect (fun r -> r.Locations)

    let title = locations |> Seq.length |> sprintf "%d Reference(s)"

    let command =
        {
            Title = title
            Command = "csharp.showReferences"
            Arguments = None // TODO: we really want to pass some more info to the client
        }

    return { data.Parameters with Command=Some command } |> success
}

let handleTextDocumentDefinition(data: Data<Types.TextDocumentPositionParams>) : AsyncLspResult<Types.GotoResult option> = async {
    let docMaybe = data.Scope.GetAnyDocumentForUri data.Parameters.TextDocument.Uri

    return!
        match docMaybe with
        | Some doc -> async {
            let! ct = Async.CancellationToken
            let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask
            let position = sourceText.Lines.GetPosition(LinePosition(data.Parameters.Position.Line, data.Parameters.Position.Character))
            let! symbolMaybe = SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct) |> Async.AwaitTask

            let symbols =
                match Option.ofObj symbolMaybe with
                | Some sym -> [sym]
                | None -> []

            let! locations = data.Scope.ResolveSymbolLocations doc.Project symbols

            return locations |> Array.ofSeq |> GotoResult.Multiple |> Some |> success }

        | None ->
            None |> success |> async.Return
}

let handleTextDocumentImplementation(data: Data<Types.TextDocumentPositionParams>): AsyncLspResult<Types.GotoResult option> = async {
    let docMaybe = data.Scope.GetAnyDocumentForUri data.Parameters.TextDocument.Uri

    return!
        match docMaybe with
        | Some doc -> async {
            let! ct = Async.CancellationToken
            let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask
            let position = sourceText.Lines.GetPosition(LinePosition(data.Parameters.Position.Line, data.Parameters.Position.Character))
            let! symbolMaybe = SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct) |> Async.AwaitTask

            let! symbols = async {
                match Option.ofObj symbolMaybe with
                | Some sym ->
                    let! implSymbols =
                        SymbolFinder.FindImplementationsAsync(sym, data.Scope.Solution)
                        |> Async.AwaitTask

                    return implSymbols |> List.ofSeq
                | None -> return []
            }

            let! locations =
                data.Scope.ResolveSymbolLocations doc.Project symbols

            return locations |> Array.ofSeq |> GotoResult.Multiple |> Some |> success }

        | None -> async {
            return None |> success }
}

let handleTextDocumentCompletion(data: Data<Types.CompletionParams>): AsyncLspResult<Types.CompletionList option> = async {
    let docMaybe = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    match docMaybe with
    | Some doc ->
        let! ct = Async.CancellationToken
        let! docText = doc.GetTextAsync(ct) |> Async.AwaitTask
        let posInText = docText.Lines.GetPosition(LinePosition(data.Parameters.Position.Line, data.Parameters.Position.Character))

        let completionService = CompletionService.GetService(doc)
        if isNull completionService then
            return ()

        let! maybeCompletionResults =
            completionService.GetCompletionsAsync(doc, posInText) |> Async.AwaitTask

        match Option.ofObj maybeCompletionResults with
        | Some completionResults ->
            let makeLspCompletionItem (item: Microsoft.CodeAnalysis.Completion.CompletionItem) =
                let baseCompletionItem = CompletionItem.Create(item.DisplayText)

                { baseCompletionItem with
                    Kind             = item.Tags |> Seq.head |> roslynTagToLspCompletion |> Some
                    SortText         = item.SortText |> Option.ofObj
                    FilterText       = item.FilterText |> Option.ofObj
                    InsertTextFormat = Some Types.InsertTextFormat.PlainText
                }

            let completionItems =
                completionResults.ItemsList
                |> Seq.map makeLspCompletionItem
                |> Array.ofSeq

            let completionList = {
                IsIncomplete = false
                Items        = completionItems
            }

            return success (Some completionList)
        | None -> return success None

    | None -> return success None
}

let handleTextDocumentDocumentHighlight(data: Data<Types.TextDocumentPositionParams>): AsyncLspResult<Types.DocumentHighlight [] option> = async {
    let getSymbolLocations symbol doc solution = async {
        let docSet = ImmutableHashSet<Document>.Empty.Add(doc)
        let! ct = Async.CancellationToken
        let! refs = SymbolFinder.FindReferencesAsync(symbol, solution, docSet, ct) |> Async.AwaitTask
        let locationsFromRefs = refs |> Seq.collect (fun r -> r.Locations) |> Seq.map (fun rl -> rl.Location)

        let! defRef = SymbolFinder.FindSourceDefinitionAsync(symbol, solution, ct) |> Async.AwaitTask

        let locationsFromDef =
            match Option.ofObj defRef with
            // TODO: we might need to skip locations that are on a different document than this one
            | Some sym -> sym.Locations |> List.ofSeq
            | None -> []

        return (Seq.append locationsFromRefs locationsFromDef)
            |> Seq.map (fun l -> {
                Range = (lspLocationForRoslynLocation l).Range;
                Kind = Some DocumentHighlightKind.Read })
    }

    let shouldHighlight (symbol: ISymbol) =
        match symbol with
        | :? INamespaceSymbol -> false
        | _ -> true

    let! maybeSymbol = data.Scope.GetSymbolAtPositionOnAnyDocument data.Parameters.TextDocument.Uri data.Parameters.Position

    match maybeSymbol with
    | Some (symbol, doc, _) ->
        if shouldHighlight symbol then
            let! locations = getSymbolLocations symbol doc data.Scope.Solution
            return locations |> Array.ofSeq |> Some |> success
        else
            return None |> success

    | None -> return None |> success
}

let handleTextDocumentDocumentSymbol(data: Data<Types.DocumentSymbolParams>): AsyncLspResult<Types.DocumentSymbol [] option> = async {
    let canEmitDocSymbolHierarchy =
        data.Scope.ClientCapabilities
        |> Option.bind (fun cc -> cc.TextDocument)
        |> Option.bind (fun cc -> cc.DocumentSymbol)
        |> Option.bind (fun cc -> cc.HierarchicalDocumentSymbolSupport)
        |> Option.defaultValue false

    let docMaybe = data.Scope.GetAnyDocumentForUri data.Parameters.TextDocument.Uri
    match docMaybe with
    | Some doc ->
        let! ct = Async.CancellationToken
        let! semanticModel = doc.GetSemanticModelAsync(ct) |> Async.AwaitTask
        let! docText = doc.GetTextAsync(ct) |> Async.AwaitTask
        let! syntaxTree = doc.GetSyntaxTreeAsync(ct) |> Async.AwaitTask

        let collector = DocumentSymbolCollector(docText, semanticModel)
        collector.Init(doc.Name)
        collector.Visit(syntaxTree.GetRoot())

        return collector.GetDocumentSymbols(canEmitDocSymbolHierarchy)
            |> Some
            |> success

    | None ->
        return None |> success
}

let handleTextDocumentHover(data: Data<Types.TextDocumentPositionParams>): AsyncLspResult<Types.Hover option> = async {
    let! maybeSymbol = data.Scope.GetSymbolAtPositionOnAnyDocument data.Parameters.TextDocument.Uri data.Parameters.Position

    let! contents =
        match maybeSymbol with
        | Some (sym, doc, pos) -> async {
            let! ct = Async.CancellationToken
            let! semanticModel = doc.GetSemanticModelAsync(ct) |> Async.AwaitTask

            return Documentation.markdownDocForSymbolWithSignature sym semanticModel pos
                |> fun s -> MarkedString.WithLanguage { Language = "markdown"; Value = s }
                |> fun s -> [s] }
        | _ -> async { return [] }

    return Some {
        Contents = contents |> Array.ofSeq |> MarkedStrings
        Range = None } |> success
}

let handleTextDocumentReferences(data: Data<Types.ReferenceParams>): AsyncLspResult<Types.Location [] option> = async {
    let! maybeSymbol = data.Scope.GetSymbolAtPositionOnAnyDocument data.Parameters.TextDocument.Uri data.Parameters.Position
    match maybeSymbol with
    | Some (symbol, _, _) ->
        let! ct = Async.CancellationToken
        let! refs = SymbolFinder.FindReferencesAsync(symbol, data.Scope.Solution, ct) |> Async.AwaitTask
        return refs
            |> Seq.collect (fun r -> r.Locations)
            |> Seq.map (fun rl -> lspLocationForRoslynLocation rl.Location)
            |> Array.ofSeq
            |> Some
            |> success

    | None -> return None |> success
}

let handleTextDocumentPrepareRename(data: Data<PrepareRenameParams>): AsyncLspResult<PrepareRenameResult option> =
    async {
        let! ct = Async.CancellationToken
        let! docMaybe =
            getDocumentForUriFromCurrentState
                UserDocument
                data.Parameters.TextDocument.Uri
                data.StateActor

        let! prepareResult =
            match docMaybe with
            | Some doc -> async {
                let! docSyntaxTree = doc.GetSyntaxTreeAsync(ct) |> Async.AwaitTask
                let! docText = doc.GetTextAsync() |> Async.AwaitTask

                let position = docText.Lines.GetPosition(LinePosition(data.Parameters.Position.Line, data.Parameters.Position.Character))
                let! symbolMaybe = SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct) |> Async.AwaitTask
                let symbolIsFromMetadata =
                    symbolMaybe
                    |> Option.ofObj
                    |> Option.map (fun s -> s.MetadataToken <> 0)
                    |> Option.defaultValue false

                let linePositionSpan = LinePositionSpan(
                    roslynLinePositionForLspPosition docText.Lines data.Parameters.Position,
                    roslynLinePositionForLspPosition docText.Lines data.Parameters.Position)

                let textSpan = docText.Lines.GetTextSpan(linePositionSpan)

                let! rootNode = docSyntaxTree.GetRootAsync() |> Async.AwaitTask
                let nodeOnPos = rootNode.FindNode(textSpan, findInsideTrivia=false, getInnermostNodeForTie=true)

                let! spanMaybe =
                    match nodeOnPos with
                    | :? PropertyDeclarationSyntax as propDec              -> propDec.Identifier.Span        |> Some |> async.Return
                    | :? MethodDeclarationSyntax as methodDec              -> methodDec.Identifier.Span      |> Some |> async.Return
                    | :? BaseTypeDeclarationSyntax as typeDec              -> typeDec.Identifier.Span        |> Some |> async.Return
                    | :? VariableDeclaratorSyntax as varDec                -> varDec.Identifier.Span         |> Some |> async.Return
                    | :? EnumMemberDeclarationSyntax as enumMemDec         -> enumMemDec.Identifier.Span     |> Some |> async.Return
                    | :? ParameterSyntax as paramSyn                       -> paramSyn.Identifier.Span       |> Some |> async.Return
                    | :? NameSyntax as nameSyn                             -> nameSyn.Span                   |> Some |> async.Return
                    | :? SingleVariableDesignationSyntax as designationSyn -> designationSyn.Identifier.Span |> Some |> async.Return
                    | :? ForEachStatementSyntax as forEachSyn              -> forEachSyn.Identifier.Span     |> Some |> async.Return
                    | :? LocalFunctionStatementSyntax as localFunStSyn     -> localFunStSyn.Identifier.Span  |> Some |> async.Return
                    | node -> async {
                        do! data.LogMessage(sprintf "handleTextDocumentPrepareRename: unhandled Type=%s" (string (node.GetType().Name)))
                        return None }

                let rangeWithPlaceholderMaybe: PrepareRenameResult option =
                    match spanMaybe, symbolIsFromMetadata with
                    | Some span, false ->
                        let range =
                            docText.Lines.GetLinePositionSpan(span)
                            |> lspRangeForRoslynLinePosSpan

                        let text = docText.GetSubText(span) |> string
                                                
                        { Range = range; Placeholder = text }
                        |> Types.PrepareRenameResult.RangeWithPlaceholder
                        |> Some
                    | _, _ ->
                        None

                return rangeWithPlaceholderMaybe }
            | None -> None |> async.Return

        return prepareResult |> success
    }

let handleTextDocumentRename(data: Data<Types.RenameParams>): AsyncLspResult<Types.WorkspaceEdit option> = async {
    let! ct = Async.CancellationToken

    let renameSymbolInDoc symbol (doc: Document) = async {
        let originalSolution = doc.Project.Solution

        let! updatedSolution =
            Renamer.RenameSymbolAsync(
                doc.Project.Solution,
                symbol,
                SymbolRenameOptions(RenameOverloads=true, RenameFile=true),
                data.Parameters.NewName,
                ct)
            |> Async.AwaitTask

        let! docTextEdit =
            lspDocChangesFromSolutionDiff
                originalSolution
                updatedSolution
                data.Scope.OpenDocVersions.TryFind
                data.LogMessage
                doc
        return docTextEdit
    }

    let! maybeSymbol = data.Scope.GetSymbolAtPositionOnUserDocument data.Parameters.TextDocument.Uri data.Parameters.Position

    let! docChanges =
        match maybeSymbol with
        | Some (symbol, doc, _) -> renameSymbolInDoc symbol doc
        | None -> async { return [] }

    return WorkspaceEdit.Create (docChanges |> Array.ofList, data.Scope.ClientCapabilities.Value) |> Some |> success
}

let handleTextDocumentSignatureHelp(data: Data<Types.SignatureHelpParams>): AsyncLspResult<Types.SignatureHelp option> =
    let docMaybe = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    match docMaybe with
    | Some doc -> async {
        let! ct = Async.CancellationToken
        let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask
        let! semanticModel = doc.GetSemanticModelAsync(ct) |> Async.AwaitTask

        let position =
            LinePosition(data.Parameters.Position.Line, data.Parameters.Position.Character)
            |> sourceText.Lines.GetPosition

        let! syntaxTree = doc.GetSyntaxTreeAsync(ct) |> Async.AwaitTask
        let! root = syntaxTree.GetRootAsync() |> Async.AwaitTask

        let rec findInvocationContext (node: SyntaxNode): InvocationContext option =
            match node with
            | :? InvocationExpressionSyntax as invocation ->
                match invocation.ArgumentList.Span.Contains(position) with
                | true ->
                    Some {
                        Receiver      = invocation.Expression
                        ArgumentTypes = (invocation.ArgumentList.Arguments |> Seq.map (fun a -> semanticModel.GetTypeInfo(a.Expression))) |> List.ofSeq
                        Separators    = (invocation.ArgumentList.Arguments.GetSeparators()) |> List.ofSeq }
                | false -> findInvocationContext node.Parent

            | :? BaseObjectCreationExpressionSyntax as objectCreation ->
                let argumentListContainsPosition =
                    objectCreation.ArgumentList
                    |> Option.ofObj
                    |> Option.map (fun argList -> argList.Span.Contains(position))

                match argumentListContainsPosition with
                | Some true ->
                    Some {
                        Receiver      = objectCreation
                        ArgumentTypes = (objectCreation.ArgumentList.Arguments |> Seq.map (fun a -> semanticModel.GetTypeInfo(a.Expression))) |> List.ofSeq
                        Separators    = (objectCreation.ArgumentList.Arguments.GetSeparators()) |> List.ofSeq }
                | _ -> findInvocationContext node.Parent

            | :? AttributeSyntax as attributeSyntax ->
                let argListContainsPosition =
                    attributeSyntax.ArgumentList
                    |> Option.ofObj
                    |> Option.map (fun argList -> argList.Span.Contains(position))

                match argListContainsPosition with
                | Some true ->
                    Some {
                        Receiver      = attributeSyntax
                        ArgumentTypes = (attributeSyntax.ArgumentList.Arguments |> Seq.map (fun a -> semanticModel.GetTypeInfo(a.Expression))) |> List.ofSeq
                        Separators    = (attributeSyntax.ArgumentList.Arguments.GetSeparators()) |> List.ofSeq }

                | _ -> findInvocationContext node.Parent

            | _ ->
                match node |> Option.ofObj with
                | Some nonNullNode -> findInvocationContext nonNullNode.Parent
                | None -> None

        let invocationMaybe =
            root.FindToken(position).Parent
            |> findInvocationContext
        return
            match invocationMaybe with
            | Some invocation ->
                let methodGroup =
                    (semanticModel.GetMemberGroup(invocation.Receiver)
                                    .OfType<IMethodSymbol>())
                    |> List.ofSeq

                let methodScore (m: IMethodSymbol) =
                    if m.Parameters.Length < invocation.ArgumentTypes.Length then
                        -1
                    else
                        let maxParams = Math.Max(m.Parameters.Length, invocation.ArgumentTypes.Length)
                        let paramCountScore = maxParams - Math.Abs(m.Parameters.Length - invocation.ArgumentTypes.Length)

                        let minParams = Math.Min(m.Parameters.Length, invocation.ArgumentTypes.Length)
                        let paramMatchingScore =
                            [0..minParams-1]
                            |> Seq.map (fun pi -> (m.Parameters[pi], invocation.ArgumentTypes[pi]))
                            |> Seq.map (fun (pt, it) -> SymbolEqualityComparer.Default.Equals(pt.Type, it.ConvertedType))
                            |> Seq.map (fun m -> if m then 1 else 0)
                            |> Seq.sum

                        paramCountScore + paramMatchingScore

                let matchingMethodMaybe =
                    methodGroup
                    |> Seq.map (fun m -> (m, methodScore m))
                    |> Seq.sortByDescending snd
                    |> Seq.map fst
                    |> Seq.tryHead

                let signatureForMethod (m: IMethodSymbol) =
                    let parameters =
                        m.Parameters
                        |> Seq.map (fun p -> { Label = (string p); Documentation = None })
                        |> Array.ofSeq

                    let documentation =
                        Types.Documentation.Markup {
                            Kind = MarkupKind.Markdown
                            Value = Documentation.markdownDocForSymbol m
                        }

                    {
                        Label         = formatSymbol m true (Some semanticModel) (Some position)
                        Documentation = Some documentation
                        Parameters    = Some parameters
                    }

                let activeParameterMaybe =
                    match matchingMethodMaybe with
                    | Some _ ->
                        let mutable p = 0
                        for comma in invocation.Separators do
                            let commaBeforePos = comma.Span.Start < position
                            p <- p + (if commaBeforePos then 1 else 0)
                        Some p

                    | None -> None

                let signatureHelpResult =
                    {
                        Signatures = methodGroup
                            |> Seq.map signatureForMethod
                            |> Array.ofSeq
                        ActiveSignature = matchingMethodMaybe
                            |> Option.map (fun m -> List.findIndex ((=) m) methodGroup)
                        ActiveParameter = activeParameterMaybe
                    }

                Some signatureHelpResult |> success

            | None ->
                None |> success
        }

    | None ->
        None |> success |> async.Return

let toSemanticToken (lines: TextLineCollection) (textSpan: TextSpan, spans: IEnumerable<ClassifiedSpan>) =
    let (typeId, modifiers) =
        spans
        |> Seq.fold (fun (t, m) s ->
            if ClassificationTypeNames.AdditiveTypeNames.Contains(s.ClassificationType) then
                (t, m ||| (GetSemanticTokenModifierFlagFromClassification s.ClassificationType))
            else
                (GetSemanticTokenIdFromClassification s.ClassificationType, m)
        ) (None, 0u)
    let pos = lines.GetLinePositionSpan(textSpan)
    (uint32 pos.Start.Line, uint32 pos.Start.Character, uint32 (pos.End.Character - pos.Start.Character), typeId, modifiers)

let computePosition (((pLine, pChar, _, _, _), (cLine, cChar, cLen, cToken, cModifiers)): ((uint32 * uint32 * uint32 * uint32 * uint32) * (uint32 * uint32 * uint32 * uint32 * uint32))) =
    let deltaLine = cLine - pLine
    let deltaChar =
        if deltaLine = 0u then
            cChar - pChar
        else
            cChar
    (deltaLine, deltaChar, cLen, cToken, cModifiers)

let getSemanticTokensRange (scope: ServerRequestScope) (uri: string) (range: Range option): AsyncLspResult<Types.SemanticTokens option> = async {
    let docMaybe = scope.GetUserDocumentForUri uri
    match docMaybe with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let textSpan =
            match range with
            | Some r ->
                r
                |> roslynLinePositionSpanForLspRange sourceText.Lines
                |> sourceText.Lines.GetTextSpan
            | None ->
                TextSpan(0, sourceText.Length)
        let! spans = Classifier.GetClassifiedSpansAsync(doc, textSpan) |> Async.AwaitTask
        let tokens =
            spans
            |> Seq.groupBy (fun span -> span.TextSpan)
            |> Seq.map (toSemanticToken sourceText.Lines)
            |> Seq.filter (fun (_, _, _, oi, _) -> Option.isSome oi)
            |> Seq.map (fun (line, startChar, len, tokenId, modifiers) -> (line, startChar, len, Option.get tokenId, modifiers))

        let response = {
            Data =
                Seq.zip (seq {yield (0u,0u,0u,0u,0u); yield! tokens}) tokens
                |> Seq.map computePosition
                |> Seq.map (fun (a,b,c,d,e) -> [a;b;c;d;e])
                |> Seq.concat
                |> Seq.toArray
            ResultId = None }
        return Some response |> success
}

let handleSemanticTokensFull(data: Data<Types.SemanticTokensParams>): AsyncLspResult<Types.SemanticTokens option> =
    getSemanticTokensRange data.Scope data.Parameters.TextDocument.Uri None

let handleSemanticTokensRange(data: Data<Types.SemanticTokensRangeParams>): AsyncLspResult<Types.SemanticTokens option> =
    getSemanticTokensRange data.Scope data.Parameters.TextDocument.Uri (Some data.Parameters.Range)

let toInlayHint (semanticModel: SemanticModel) (lines: TextLineCollection) (node: SyntaxNode): InlayHint option =
    let validateType (ty: ITypeSymbol) =
        if isNull ty || ty :? IErrorTypeSymbol || ty.Name = "var" then
            None
        else
            Some ty
    let typeDisplayStyle = SymbolDisplayFormat(
        genericsOptions = SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions = (SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral
            ||| SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            ||| SymbolDisplayMiscellaneousOptions.UseSpecialTypes))
    let toTypeInlayHint (pos: int) (ty: ITypeSymbol): InlayHint =
        {
            Position = pos |> lines.GetLinePosition |> lspPositionForRoslynLinePosition
            Label = InlayHintLabel.String (": " + ty.ToDisplayString(typeDisplayStyle))
            Kind = Some InlayHintKind.Type
            TextEdits = None
            Tooltip = None
            PaddingLeft = Some false
            PaddingRight = Some false
            Data = None
        }

    let validateParameter (arg: SyntaxNode) (par: IParameterSymbol) =
        match arg.Parent with
        // Don't show hint for indexer
        | :? BracketedArgumentListSyntax -> None
        // Don't show hint if parameter name is empty
        | _ when String.IsNullOrEmpty(par.Name) -> None
        // Don't show hint if argument matches parameter name
        | _ when String.Equals(arg.GetText().ToString(), par.Name, StringComparison.CurrentCultureIgnoreCase) -> None
        | _ -> Some par
    let toParameterInlayHint (pos: int) (par: IParameterSymbol): InlayHint =
        {
            Position = pos |> lines.GetLinePosition |> lspPositionForRoslynLinePosition
            Label = InlayHintLabel.String (par.Name + ":")
            Kind = Some InlayHintKind.Parameter
            TextEdits = None
            Tooltip = None
            PaddingLeft = Some false
            PaddingRight = Some true
            Data = None
        }

    // It's a rewrite of https://github.com/dotnet/roslyn/blob/main/src/Features/CSharp/Portable/InlineHints/CSharpInlineTypeHintsService.cs &
    // https://github.com/dotnet/roslyn/blob/main/src/Features/CSharp/Portable/InlineHints/CSharpInlineParameterNameHintsService.cs.
    // If Roslyn exposes the classes, then we can just do a type convert.
    match node with
    | :? VariableDeclarationSyntax as var when
        var.Type.IsVar && var.Variables.Count = 1 && not var.Variables[0].Identifier.IsMissing
        ->
        semanticModel.GetTypeInfo(var.Type).Type
        |> validateType
        |> Option.map (toTypeInlayHint var.Variables[0].Identifier.Span.End)
    // We handle individual variables of ParenthesizedVariableDesignationSyntax separately.
    // For example, in `var (x, y) = (0, "")`, we should `int` for `x` and `string` for `y`.
    // It's redundant to show `(int, string)` for `var`
    | :? DeclarationExpressionSyntax as dec when
        dec.Type.IsVar && not (dec.Designation :? ParenthesizedVariableDesignationSyntax)
        ->
        semanticModel.GetTypeInfo(dec.Type).Type
        |> validateType
        |> Option.map (toTypeInlayHint dec.Designation.Span.End)
    | :? SingleVariableDesignationSyntax as var when
        not (var.Parent :? DeclarationPatternSyntax) && not (var.Parent :? DeclarationExpressionSyntax)
        ->
        semanticModel.GetDeclaredSymbol(var)
        |> fun sym ->
            match sym with
            | :? ILocalSymbol as local -> Some local
            | _ -> None
        |> Option.map (fun local -> local.Type)
        |> Option.bind validateType
        |> Option.map (toTypeInlayHint var.Identifier.Span.End)
    | :? ForEachStatementSyntax as forEach when
        forEach.Type.IsVar
        ->
        semanticModel.GetForEachStatementInfo(forEach).ElementType
        |> validateType
        |> Option.map (toTypeInlayHint forEach.Identifier.Span.End)
    | :? ParameterSyntax as parameterNode when
        isNull parameterNode.Type
        ->
        let parameter = semanticModel.GetDeclaredSymbol(parameterNode)
        parameter
        |> fun parameter ->
            match parameter.ContainingSymbol with
            | :? IMethodSymbol as methSym when methSym.MethodKind = MethodKind.AnonymousFunction -> Some parameter
            | _ -> None
        |> Option.map (fun parameter -> parameter.Type)
        |> Option.bind validateType
        |> Option.map (toTypeInlayHint parameterNode.Identifier.Span.End)
    | :? ImplicitObjectCreationExpressionSyntax as implicitNew
        ->
        semanticModel.GetTypeInfo(implicitNew).Type
        |> validateType
        |> Option.map (toTypeInlayHint implicitNew.NewKeyword.Span.End)

    | :? ArgumentSyntax as argument when
        isNull argument.NameColon
        ->
        argument
        |> getParameterForArgumentSyntax semanticModel
        |> Option.bind (validateParameter node)
        |> Option.map (toParameterInlayHint argument.Span.Start)
    | :? AttributeArgumentSyntax as argument when
        isNull argument.NameEquals && isNull argument.NameColon
        ->
        argument
        |> getParameterForAttributeArgumentSyntax semanticModel
        |> Option.bind (validateParameter node)
        |> Option.map (toParameterInlayHint argument.Span.Start)

    | _ -> None

let handleTextDocumentInlayHint(data: Data<InlayHintParams>): AsyncLspResult<InlayHint [] option> = async {
    match data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri with
    | None -> return None |> success
    | Some doc ->
        let! semanticModel = doc.GetSemanticModelAsync() |> Async.AwaitTask
        let! root = doc.GetSyntaxRootAsync() |> Async.AwaitTask
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let textSpan =
            data.Parameters.Range
            |> roslynLinePositionSpanForLspRange sourceText.Lines
            |> sourceText.Lines.GetTextSpan

        let inlayHints =
            root.DescendantNodes(textSpan, fun node -> node.Span.IntersectsWith(textSpan))
            |> Seq.map (toInlayHint semanticModel sourceText.Lines)
            |> Seq.filter Option.isSome
            |> Seq.map Option.get
        return inlayHints |> Seq.toArray |> Some |> success
}

let toHierarchyItem (symbol: ISymbol) (location: Location): HierarchyItem =
    let displayStyle = SymbolDisplayFormat(
        typeQualificationStyle = SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions = SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions = (SymbolDisplayMemberOptions.IncludeParameters
            ||| SymbolDisplayMemberOptions.IncludeExplicitInterface),
        parameterOptions = (SymbolDisplayParameterOptions.IncludeParamsRefOut
            ||| SymbolDisplayParameterOptions.IncludeExtensionThis
            ||| SymbolDisplayParameterOptions.IncludeType),
        miscellaneousOptions = SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
    let (_, kind) = getSymbolNameAndKind None None symbol
    let containingType = (symbol.ContainingType :> ISymbol) |> Option.ofObj
    let containingNamespace = (symbol.ContainingNamespace :> ISymbol) |> Option.ofObj
    {
        Name = symbol.ToDisplayString(displayStyle)
        Kind = kind
        Tags = None
        Detail = containingType |> Option.orElse containingNamespace |> Option.map (fun sym -> sym.ToDisplayString())
        Uri = location.Uri
        Range = location.Range
        SelectionRange = location.Range
        Data = None
    }

let handleTextDocumentPrepareTypeHierarchy(data: Data<TypeHierarchyPrepareParams>): AsyncLspResult<TypeHierarchyItem [] option> = async {
    match data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let position =
            data.Parameters.Position
            |> roslynLinePositionForLspPosition sourceText.Lines
            |> sourceText.Lines.GetPosition
        let symbol =
            SymbolFinder.FindSymbolAtPositionAsync(doc, position)
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Option.ofObj
            |> Option.filter (fun sym -> sym :? INamedTypeSymbol)
            |> Option.toList
        let! locations = data.Scope.ResolveSymbolLocations doc.Project symbol
        return
            Seq.allPairs symbol locations
            |> Seq.map (uncurry toHierarchyItem)
            |> Seq.toArray
            |> Some
            |> success
}

let handleTypeHierarchySupertypes(data: Data<TypeHierarchySupertypesParams>): AsyncLspResult<TypeHierarchyItem [] option> = async {
    match data.Scope.GetUserDocumentForUri data.Parameters.Item.Uri with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let position =
            data.Parameters.Item.Range.Start
            |> roslynLinePositionForLspPosition sourceText.Lines
            |> sourceText.Lines.GetPosition
        let symbol =
            SymbolFinder.FindSymbolAtPositionAsync(doc, position)
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Option.ofObj
            |> Option.bind (fun sym ->
                match sym with
                | :? INamedTypeSymbol as namedType -> Some namedType
                | _ -> None)
        let baseType =
            symbol
            |> Option.bind (fun sym -> Option.ofObj sym.BaseType)
            |> Option.filter (fun sym -> sym.SpecialType = SpecialType.None)
            |> Option.toList
        let interfaces =
            symbol
            |> Option.toList
            |> List.collect (fun sym -> Seq.toList sym.Interfaces)
        let supertypes = baseType @ interfaces
        return
            supertypes
            |> Seq.map (fun sym -> data.Scope.ResolveSymbolLocations doc.Project [sym])
            |> Seq.map Async.RunSynchronously
            |> Seq.zip supertypes
            |> Seq.collect (fun (sym, locs) -> Seq.map (fun loc -> (sym, loc)) locs)
            |> Seq.map (uncurry toHierarchyItem)
            |> Seq.toArray
            |> Some
            |> success
}

let handleTypeHierarchySubtypes(data: Data<TypeHierarchySubtypesParams>): AsyncLspResult<TypeHierarchyItem [] option> = async {
    match data.Scope.GetUserDocumentForUri data.Parameters.Item.Uri with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let position =
            data.Parameters.Item.Range.Start
            |> roslynLinePositionForLspPosition sourceText.Lines
            |> sourceText.Lines.GetPosition
        let symbol =
            SymbolFinder.FindSymbolAtPositionAsync(doc, position)
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Option.ofObj
            |> Option.bind (fun sym ->
                match sym with
                | :? INamedTypeSymbol as namedType -> Some namedType
                | _ -> None)
            |> Option.toList
        let derivedClasses =
            symbol
            |> Seq.collect (fun sym -> SymbolFinder.FindDerivedClassesAsync(sym, data.Scope.Solution, false) |> Async.AwaitTask |> Async.RunSynchronously)
            |> Seq.toList
        let derivedInterfaces =
            symbol
            |> Seq.collect (fun sym -> SymbolFinder.FindDerivedInterfacesAsync(sym, data.Scope.Solution, false) |> Async.AwaitTask |> Async.RunSynchronously)
            |> Seq.toList
        let implementations =
            symbol
            |> Seq.collect (fun sym -> SymbolFinder.FindImplementationsAsync(sym, data.Scope.Solution, false) |> Async.AwaitTask |> Async.RunSynchronously)
            |> Seq.toList
        let subtypes = derivedClasses @ derivedInterfaces @ implementations
        return
            subtypes
            |> Seq.map (fun sym -> data.Scope.ResolveSymbolLocations doc.Project [sym])
            |> Seq.map Async.RunSynchronously
            |> Seq.zip subtypes
            |> Seq.collect (fun (sym, locs) -> Seq.map (fun loc -> (sym, loc)) locs)
            |> Seq.map (uncurry toHierarchyItem)
            |> Seq.toArray
            |> Some
            |> success
}

let handleTextDocumentPrepareCallHierarchy(data: Data<CallHierarchyPrepareParams>): AsyncLspResult<CallHierarchyItem [] option> = async {
    match data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let position =
            data.Parameters.Position
            |> roslynLinePositionForLspPosition sourceText.Lines
            |> sourceText.Lines.GetPosition
        let symbols =
            SymbolFinder.FindSymbolAtPositionAsync(doc, position)
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Option.ofObj
            |> Option.filter isCallableSymbol
            |> Option.toList
        let! locations = data.Scope.ResolveSymbolLocations doc.Project symbols
        return Seq.allPairs symbols locations
            |> Seq.map (uncurry toHierarchyItem)
            |> Seq.toArray
            |> Some
            |> success
}

let handleCallHierarchyIncomingCalls(data: Data<CallHierarchyIncomingCallsParams>): AsyncLspResult<CallHierarchyIncomingCall [] option> = async {
    let toCallHierarchyIncomingCalls (info: SymbolCallerInfo): CallHierarchyIncomingCall seq =
        let fromRanges = info.Locations |> Seq.map (fun l -> l.GetLineSpan().Span |> lspRangeForRoslynLinePosSpan) |> Seq.toArray
        info.CallingSymbol.Locations
        |> Seq.map (fun loc ->
            {
                From = toHierarchyItem (info.CallingSymbol) (loc |> lspLocationForRoslynLocation)
                FromRanges = fromRanges
            })

    match data.Scope.GetUserDocumentForUri data.Parameters.Item.Uri with
    | None -> return None |> success
    | Some doc ->
        let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
        let position =
            data.Parameters.Item.Range.Start
            |> roslynLinePositionForLspPosition sourceText.Lines
            |> sourceText.Lines.GetPosition
        let! symbol = SymbolFinder.FindSymbolAtPositionAsync(doc, position) |> Async.AwaitTask
        let callers =
            symbol
            |> Option.ofObj
            |> Option.toList
            |> Seq.collect (fun sym -> SymbolFinder.FindCallersAsync(sym, data.Scope.Solution) |> Async.AwaitTask |> Async.RunSynchronously)
            |> Seq.filter (fun info -> info.IsDirect && isCallableSymbol info.CallingSymbol)
        return callers
            |> Seq.collect toCallHierarchyIncomingCalls
            |> Seq.toArray
            |> Some
            |> success
}

let handleCallHierarchyOutgoingCalls(data: Data<CallHierarchyOutgoingCallsParams>): AsyncLspResult<CallHierarchyOutgoingCall [] option> = async {
    // TODO: There is no memthod of SymbolFinder which can find all outgoing calls of a specific symbol. Then how can we implement it? Parsing AST manually?
    return None |> success
}

let handleWorkspaceSymbol(data: Data<Types.WorkspaceSymbolParams>): AsyncLspResult<Types.SymbolInformation [] option> = async {
    let! symbols = findSymbolsInSolution data.Scope.Solution data.Parameters.Query (Some 20)
    return symbols |> Array.ofSeq |> Some |> success
}

let handleWorkspaceDidChangeWatchedFiles(data: Data<Types.DidChangeWatchedFilesParams>): Async<LspResult<unit>> = async {
    let tryReloadDocumentOnUri uri = async {
        match data.Scope.GetDocumentForUriOfType UserDocument uri with
        | Some (doc, _) ->
            let fileText = uri |> Util.parseFileUri |> File.ReadAllText
            let updatedDoc = SourceText.From(fileText) |> doc.WithText

            data.Scope.Emit(SolutionChange updatedDoc.Project.Solution)
            data.Diagnostics.Post(DocumentBacklogUpdate)

        | None ->
            let docFilePathMaybe = uri |> Util.tryParseFileUri
            match docFilePathMaybe with
            | Some docFilePath ->
                // ok, this document is not on solution, register a new one
                let fileText = docFilePath |> File.ReadAllText
                let! newDocMaybe =
                    tryAddDocument
                        data.LogMessage
                        docFilePath
                        fileText
                        data.Scope.Solution
                match newDocMaybe with
                | Some newDoc ->
                    data.Scope.Emit(SolutionChange newDoc.Project.Solution)
                    data.Diagnostics.Post(DocumentBacklogUpdate)
                | None -> ()
            | None -> ()
    }

    for change in data.Parameters.Changes do
        match Path.GetExtension(change.Uri) with
        | ".csproj" ->
            do! data.LogMessage "change to .csproj detected, will reload solution"
            data.Scope.Emit(SolutionReloadRequest (TimeSpan.FromSeconds(5)))

        | ".sln" ->
            do! data.LogMessage "change to .sln detected, will reload solution"
            data.Scope.Emit(SolutionReloadRequest (TimeSpan.FromSeconds(5)))

        | ".cs" ->
            match change.Type with
            | FileChangeType.Created ->
                do! tryReloadDocumentOnUri change.Uri

            | FileChangeType.Changed ->
                do! tryReloadDocumentOnUri change.Uri

            | FileChangeType.Deleted ->
                match data.Scope.GetDocumentForUriOfType UserDocument change.Uri with
                | Some (existingDoc, _) ->
                    let updatedProject = existingDoc.Project.RemoveDocument(existingDoc.Id)

                    data.Scope.Emit(SolutionChange updatedProject.Solution)
                    data.Scope.Emit(OpenDocVersionRemove change.Uri)

                    data.Diagnostics.Post(DocumentRemoval change.Uri)
                | None -> ()
            | _ -> ()

        | _ -> ()

    return LspResult.Ok()
}

let handleWorkspaceDidChangeConfiguration(data: Data<DidChangeConfigurationParams>): Async<LspResult<unit>> =
    async {
        let csharpSettings =
            data.Parameters.Settings
            |> deserialize<ServerSettingsDto>
            |> (fun x -> x.csharp)
            |> Option.defaultValue ServerSettingsCSharpDto.Default

        let newServerSettings = { data.Scope.State.Settings with SolutionPath = csharpSettings.solution }
        data.Scope.Emit(SettingsChange newServerSettings)

        return LspResult.Ok ()
    }

let handleCSharpMetadata(data: Data<CSharpMetadataParams>): AsyncLspResult<CSharpMetadataResponse option> = async {
    let uri = data.Parameters.TextDocument.Uri

    let metadataMaybe =
        data.Scope.DecompiledMetadata
        |> Map.tryFind uri
        |> Option.map (fun x -> x.Metadata)

    return metadataMaybe |> success
}

let handleTextDocumentFormatting(data: Data<Types.DocumentFormattingParams>): AsyncLspResult<Types.TextEdit [] option> = async {
    let maybeDocument = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    let! formattingChanges =
        match maybeDocument with
        | Some doc -> handleTextDocumentFormatAsync doc
        | None -> Array.empty |> async.Return
    return formattingChanges |> Some |> success
}

let handleTextDocumentRangeFormatting(data: Data<DocumentRangeFormattingParams>): AsyncLspResult<TextEdit[] option> = async {
    let maybeDocument = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    let! formattingChanges =
        match maybeDocument with
        | Some doc -> handleTextDocumentRangeFormatAsync doc data.Parameters.Range
        | None -> Array.empty |> async.Return
    return formattingChanges |> Some |> success
}

let handleTextDocumentOnTypeFormatting(data: Data<DocumentOnTypeFormattingParams>): AsyncLspResult<TextEdit[] option> = async {
    let maybeDocument = data.Scope.GetUserDocumentForUri data.Parameters.TextDocument.Uri
    let! formattingChanges =
        match maybeDocument with
        | Some doc -> handleTextOnTypeFormatAsync doc data.Parameters.Ch data.Parameters.Position
        | None -> Array.empty |> async.Return
    return formattingChanges |> Some |> success
}

let setupServerHandlers settings (lspClient: LspClient) =
    let mutable logMessageCurrent: AsyncLogFn = fun _ -> async { return() }
    let logMessageInvoke m = logMessageCurrent(m)

    let stateActor = MailboxProcessor.Start(
        serverEventLoop
            (fun m -> logMessageInvoke m)
            { emptyServerState with Settings = settings })

    let diagnostics = MailboxProcessor.Start(
        fun inbox -> async {
            let rec loop state = async {
                try
                    let! msg = inbox.Receive()
                    let doGetDocumentForUriFromCurrentState(uri: string) =
                        getDocumentForUriFromCurrentState AnyDocument uri stateActor
                    let! (newState, eventsToPost) =
                        processDiagnosticsEvent
                            logMessageInvoke
                            (fun docUri diagnostics -> lspClient.TextDocumentPublishDiagnostics { Uri = docUri; Diagnostics = diagnostics })
                            doGetDocumentForUriFromCurrentState
                            state
                            inbox.CurrentQueueLength
                            msg

                    for ev in eventsToPost do inbox.Post(ev)

                    return! loop newState
                with
                | ex ->
                    do! logMessageInvoke (sprintf "unhandled exception in `diagnostics`: %s" (ex|>string))
                    raise ex
            }

            return! loop emptyDiagnosticsState
        })

    let timer = setupTimer(diagnostics, stateActor)

    let logMessage = fun message -> logMessageWithLevel(MessageType.Log, message, lspClient)
    let logInfo = fun message -> logMessageWithLevel(MessageType.Info, message, lspClient)

    let requestHandlingWithScope requestType requestPriority nameAndAsyncFn =
        let requestName = nameAndAsyncFn |> fst
        let asyncFn = nameAndAsyncFn |> snd

        let requestHandler param =
            logMessageCurrent <- logMessage

            // we want to be careful and lock solution for change immediately w/o entering async/returing an `async` workflow
            //
            // StreamJsonRpc lib we're using in Ionide.LanguageServerProtocol guarantees that it will not call another
            // handler until previous one returns a Task (in our case -- F# `async` object.)

            let startRequest rc = StartRequest (requestName, requestType, requestPriority, rc)
            let requestId, semaphore = stateActor.PostAndReply(startRequest)

            let stateAcquisitionAndHandlerInvocation = async {
                do! semaphore.WaitAsync() |> Async.AwaitTask

                let! state = stateActor.PostAndAsyncReply(GetState)

                let scope = ServerRequestScope(requestId, state, stateActor.Post, logMessage)

                return! asyncFn scope param
            }

            let wrapExceptionAsLspResult op =
                async {
                    let! resultOrExn = op |> Async.Catch

                    return
                        match resultOrExn with
                        | Choice1Of2 result -> result
                        | Choice2Of2 exn ->
                            match exn with
                            | :? TaskCanceledException -> LspResult.requestCancelled
                            | :? OperationCanceledException -> LspResult.requestCancelled
                            | _ -> LspResult.internalError (string exn)
                }

            stateAcquisitionAndHandlerInvocation
            |> wrapExceptionAsLspResult
            |> unwindProtect (fun () -> stateActor.Post(FinishRequest requestId))

        (requestName, requestHandler |> requestHandling)

    let requestHandlingWithReadOnlyScope nameAndAsyncFn =
        requestHandlingWithScope ReadOnly 0 nameAndAsyncFn

    let requestHandlingWithReadOnlyScopeWithPriority priority nameAndAsyncFn =
        requestHandlingWithScope ReadOnly priority nameAndAsyncFn

    let requestHandlingWithReadWriteScope nameAndAsyncFn =
        requestHandlingWithScope ReadWrite 0 nameAndAsyncFn

    let on(func: (Data<'T>) -> AsyncLspResult<'U>): (ServerRequestScope -> 'T -> Async<LspResult<'U>>) = fun (scope: ServerRequestScope) (parameters: 'T) ->
        func({
            Scope = scope;
            Parameters = parameters;
            LspClient = lspClient;
            StateActor = stateActor;
            Diagnostics = diagnostics;
            LogInfo = logInfo;
            LogMessage = logMessage
        })

    [
        ("initialize"                       , on(handleInitialize))                       |> requestHandlingWithReadWriteScope
        ("initialized"                      , on(handleInitialized))                      |> requestHandlingWithReadWriteScope
        ("textDocument/didOpen"             , on(handleTextDocumentDidOpen))              |> requestHandlingWithReadWriteScope
        ("textDocument/didChange"           , on(handleTextDocumentDidChange))            |> requestHandlingWithReadWriteScope
        ("textDocument/didClose"            , on(handleTextDocumentDidClose))             |> requestHandlingWithReadWriteScope
        ("textDocument/didSave"             , on(handleTextDocumentDidSave))              |> requestHandlingWithReadWriteScope
        ("textDocument/codeAction"          , on(handleTextDocumentCodeAction))           |> requestHandlingWithReadOnlyScope
        ("codeAction/resolve"               , on(handleCodeActionResolve))                |> requestHandlingWithReadOnlyScope
        ("textDocument/codeLens"            , on(handleTextDocumentCodeLens))             |> requestHandlingWithReadOnlyScopeWithPriority 99
        ("codeLens/resolve"                 , on(handleCodeLensResolve))                  |> requestHandlingWithReadOnlyScope
        ("textDocument/completion"          , on(handleTextDocumentCompletion))           |> requestHandlingWithReadOnlyScope
        ("textDocument/definition"          , on(handleTextDocumentDefinition))           |> requestHandlingWithReadOnlyScope
        ("textDocument/documentHighlight"   , on(handleTextDocumentDocumentHighlight))    |> requestHandlingWithReadOnlyScope
        ("textDocument/documentSymbol"      , on(handleTextDocumentDocumentSymbol))       |> requestHandlingWithReadOnlyScope
        ("textDocument/hover"               , on(handleTextDocumentHover))                |> requestHandlingWithReadOnlyScope
        ("textDocument/implementation"      , on(handleTextDocumentImplementation))       |> requestHandlingWithReadOnlyScope
        ("textDocument/formatting"          , on(handleTextDocumentFormatting))           |> requestHandlingWithReadOnlyScope
        ("textDocument/onTypeFormatting"    , on(handleTextDocumentOnTypeFormatting))     |> requestHandlingWithReadOnlyScope
        ("textDocument/rangeFormatting"     , on(handleTextDocumentRangeFormatting))      |> requestHandlingWithReadOnlyScope
        ("textDocument/references"          , on(handleTextDocumentReferences))           |> requestHandlingWithReadOnlyScope
        ("textDocument/prepareRename"       , on(handleTextDocumentPrepareRename))        |> requestHandlingWithReadOnlyScope
        ("textDocument/rename"              , on(handleTextDocumentRename))               |> requestHandlingWithReadOnlyScope
        ("textDocument/signatureHelp"       , on(handleTextDocumentSignatureHelp))        |> requestHandlingWithReadOnlyScope
        ("textDocument/semanticTokens/full" , on(handleSemanticTokensFull))               |> requestHandlingWithReadOnlyScope
        ("textDocument/semanticTokens/range", on(handleSemanticTokensRange))              |> requestHandlingWithReadOnlyScope
        ("textDocument/inlayHint"           , on(handleTextDocumentInlayHint))            |> requestHandlingWithReadOnlyScope
        ("textDocument/prepareTypeHierarchy", on(handleTextDocumentPrepareTypeHierarchy)) |> requestHandlingWithReadOnlyScope
        ("typeHierarchy/supertypes"         , on(handleTypeHierarchySupertypes))          |> requestHandlingWithReadOnlyScope
        ("typeHierarchy/subtypes"           , on(handleTypeHierarchySubtypes))            |> requestHandlingWithReadOnlyScope
        ("textDocument/prepareCallHierarchy", on(handleTextDocumentPrepareCallHierarchy)) |> requestHandlingWithReadOnlyScope
        ("callHierarchy/incomingCalls"      , on(handleCallHierarchyIncomingCalls))       |> requestHandlingWithReadOnlyScope
        ("callHierarchy/outgoingCalls"      , on(handleCallHierarchyOutgoingCalls))       |> requestHandlingWithReadOnlyScope
        ("workspace/symbol"                 , on(handleWorkspaceSymbol))                  |> requestHandlingWithReadOnlyScope
        ("workspace/didChangeWatchedFiles"  , on(handleWorkspaceDidChangeWatchedFiles))   |> requestHandlingWithReadWriteScope
        ("workspace/didChangeConfiguration" , on(handleWorkspaceDidChangeConfiguration))  |> requestHandlingWithReadWriteScope
        ("csharp/metadata"                  , on(handleCSharpMetadata))                   |> requestHandlingWithReadOnlyScope
    ]
    |> Map.ofList

let startCore options =
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()

    Ionide.LanguageServerProtocol.Server.startWithSetup
        (setupServerHandlers options)
        input
        output
        CSharpLspClient
        defaultRpc

let start options =
    try
        let result = startCore options
        int result
    with
    | _ex ->
        // logger.error (Log.setMessage "Start - LSP mode crashed" >> Log.addExn ex)
        3