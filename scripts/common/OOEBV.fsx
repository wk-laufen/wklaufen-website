#I @"..\..\"
#r @"packages\HtmlAgilityPack\lib\Net45\HtmlAgilityPack.dll"

//#load @".\DataModels.fsx"
//#load @".\Choice.fsx"
//#load @".\Http.fsx"

open System
open System.Globalization
open System.IO
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open HtmlAgilityPack
open DataModels

// http://stackoverflow.com/a/293357
HtmlNode.ElementsFlags.Remove "option"

let private baseUri = Uri "https://mv.ooe-bv.at/"

let private loadHtmlResponse (response: System.Net.Http.HttpResponseMessage) =
    let content = Http.getContentString response
    let doc = HtmlDocument()
    doc.LoadHtml content
    doc

/// <summary>
/// Log in with username and password.
/// </summary>
/// <param name="username"></param>
/// <param name="password"></param>
/// <returns>Overview page as HTML document</returns>
let login username password =
    let uri = Uri(baseUri, "index_n1.php?slid=%3D%3DwMT0cjN1BgDN0QTMuVGZsVWbuF2Xh1zYul2M0cjN1gDN0QTM")
    let formParams = [
        "b_benutzername", username
        "b_kennwort", password
        "pw_vergessen", ""
        "anmelden", "anmelden"
    ]
    Http.postForm uri formParams
    |> Choice.bind (fun response ->
        let loginPageContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
        let redirectUrlMatch = Regex.Match(loginPageContent, @"location.href=""(?<url>[^""]+)""")
        let uri = Uri(baseUri, redirectUrlMatch.Groups.["url"].Value)
        Http.get uri
    )
    |> Choice.mapError (fun _ -> "Error while logging in")
    |> Choice.map loadHtmlResponse

let loadAndResetMemberOverviewPage (startPage: HtmlDocument) =
    startPage.DocumentNode.SelectNodes("//a[@class=\"menu_0\"]")
    |> Seq.filter (fun n -> n.InnerText.Equals("mitglieder", StringComparison.InvariantCultureIgnoreCase))
    |> Seq.exactlyOne
    |> fun n -> Uri(baseUri, n.Attributes.["href"].Value)
    |> Http.get
    |> Choice.mapError (fun _ -> sprintf "Error while loading member page")
    |> Choice.map loadHtmlResponse
    |> Choice.bind (fun memberPage ->
        let relativeUri = memberPage.DocumentNode.SelectSingleNode("//form[@name=\"frm_liste\"]").Attributes.["action"].Value
        let uri = new Uri(baseUri, relativeUri)
        let formParams = [
            "order", "mit_name,mit_vorname"
            "loeschen", ""
            "CurrentPage", "1"
            "seite_vor", ""
            "seite_rueck", ""
            "del_id", ""
            "PageSize", "1000"
            "end_suche", "Alle"
            "smit_status", ""
            "smit_name", ""
            "smit_strasse", ""
            "smit_plz", ""
            "smit_ort", ""
            "smit_funktion[]", ""
            "smit_hauptinstrument", ""
            "smit_jugend", ""
        ]
        Http.postForm uri formParams
        |> Choice.mapError (fun _ -> sprintf "Error while resetting member page")
    )
    |> Choice.map loadHtmlResponse

module private MemberParsing =
    let normalizeName (name: string) =
        name
        |> fun n -> n.Replace("&nbsp;", " ")
        |> fun n -> n.Trim()
        |> fun n -> n.ToLowerInvariant()
        |> fun n -> Regex.Replace(n, @"\b(\w)", new MatchEvaluator(fun m -> m.Value.ToUpper()))

    let isMember (status: string) =
        [ "akt"; "mim"; "mip"; "ten" ]
        |> List.exists (fun s -> status.Equals(s, StringComparison.InvariantCultureIgnoreCase))

    let parseDate dateString =
        match dateString with
        | "" -> None
        | x -> DateTime.ParseExact(x, "d", CultureInfo.GetCultureInfo("de-AT")) |> Some

    let firstNonEmptyOrNone list =
        list
        |> List.filter (not << String.IsNullOrWhiteSpace)
        |> List.tryHead

    type Gender = | Male | Female | Unspecified

    let parseGender text =
        match text with
        | "m\u00E4nnlich" -> Male
        | "weiblich" -> Female
        | _ -> Unspecified

    let genderRole gender role =
        let replacements =
            match gender with
            | Male -> [("/in", ""); ("/obfrau", "")]
            | Female -> [("/in", "in"); ("obmann/", "")]
            | Unspecified -> []

        let folder (s: string) (p: string, r: string) =
            Regex.Replace(s, p, r, RegexOptions.IgnoreCase)

        List.fold folder role replacements

let private rand = Random()

let private tryGetMemberImage memberId =
    new Uri(baseUri, sprintf "/core/inc_cms/hole_ajax_div_up.php?thumbnail_aktiv=1&cmyk_aktiv=&big_aktiv=&bereich=mitglieder_bild&bereich_tabelle=mitglieder_bild_archiv&bereich_verzeichniss=mitglieder_bild&a_id=ua&anzeige_form=&rand=%d&del_rec=x&id=%d&sprache=deu" (rand.Next()) memberId)
    |> Http.get
    |> Choice.map loadHtmlResponse
    |> Choice.map (fun doc ->
        doc.DocumentNode.SelectNodes("//img[@src]")
        |> Seq.map (fun n -> n.Attributes.["src"].Value)
        |> Seq.filter (fun src -> src.StartsWith("uploads/mitglieder_bild/", StringComparison.InvariantCultureIgnoreCase))
        |> Seq.tryHead
        |> Option.map (fun src -> new Uri(baseUri, src.Replace("_small_", "_")))
    )

let private loadMembers filterFn (memberPage: HtmlDocument) =
    memberPage.DocumentNode.SelectNodes("//table[@id=\"mytable\"]/tr[@id]")
    |> List.ofSeq
    |> List.filter filterFn
    |> List.map (fun row ->
        let memberId = row.Id.Replace("dsz_", "") |> int

        let fullName = row.SelectSingleNode("td[2]").InnerText |> MemberParsing.normalizeName
        printfn "Getting details for member %s (Id = %d)" fullName memberId

        let memberStatus = row.SelectSingleNode("td[1]").InnerText
        let isActive = MemberParsing.isMember memberStatus

        row.Descendants("a")
        |> Seq.filter(fun a ->
            a.Attributes.["href"] <> null
            && a.Attributes.["title"] <> null
            && a.Attributes.["title"].Value.Equals("bearbeiten")
        )
        |> Seq.exactlyOne
        |> fun a -> new Uri(baseUri, a.Attributes.["href"].Value)
        |> Http.get
        |> Choice.map loadHtmlResponse
        |> Choice.mapError (fun _ -> sprintf "Error while getting details document of member with id %d" memberId)
        |> Choice.bind (fun detailsDoc ->
            let getTextBoxValue name =
                let xPath = sprintf "//input[@name=\"%s\"]" name
                detailsDoc.DocumentNode.SelectSingleNode(xPath).Attributes.["value"].Value

            let memberSince =
                detailsDoc.DocumentNode.Descendants("td")
                |> Seq.filter (fun n -> n.InnerText.Equals("eintrittsdatum", StringComparison.InvariantCultureIgnoreCase))
                |> Seq.exactlyOne
                |> fun n -> n.ParentNode.SelectSingleNode("td[2]").InnerText
                |> MemberParsing.parseDate

            let firstName = getTextBoxValue "mit_vorname" |> MemberParsing.normalizeName
            let lastName = getTextBoxValue "mit_name" |> MemberParsing.normalizeName
            
            let image =
                if isActive
                then
                    tryGetMemberImage memberId
                    |> Choice.mapError (fun _ -> sprintf "Error while getting HTML document for photo for %s %s" firstName lastName)
                else
                    Choice1Of2 None
            image
            |> Choice.map (fun image ->
                let getSelectedOption (comboBox: HtmlNode) =
                    comboBox.Descendants("option")
                    |> Seq.filter (fun o -> o.Attributes.["selected"] <> null && not <| String.IsNullOrEmpty(o.Attributes.["value"].Value))
                    |> Seq.tryHead
                    |> Option.map (fun o -> MemberParsing.normalizeName o.InnerText)

                let gender =
                    detailsDoc.DocumentNode.SelectNodes("//input[@type=\"radio\"][@name=\"mit_geschlecht\"]")
                    |> Seq.tryFind(fun n -> n.Attributes.["checked"] <> null)
                    |> function
                    | Some n when n.Attributes.["value"].Value.Equals("m", StringComparison.InvariantCultureIgnoreCase) -> MemberParsing.Gender.Male
                    | Some n when n.Attributes.["value"].Value.Equals("w", StringComparison.InvariantCultureIgnoreCase) -> MemberParsing.Gender.Female
                    | _ -> MemberParsing.Gender.Unspecified
            
                let roles =
                    detailsDoc.DocumentNode.SelectNodes("//select")
                    |> Seq.filter (fun n -> n.Id.StartsWith("mf_function_", StringComparison.InvariantCultureIgnoreCase))
                    |> Seq.choose (fun cbxRole ->
                        let roleName = getSelectedOption cbxRole

                        let roleEnded =
                            cbxRole.Ancestors("tr")
                            |> Seq.head
                            |> fun p -> p.SelectSingleNode "td[3]/input[@type=\"text\"][@name]"
                            |> fun n -> not <| String.IsNullOrWhiteSpace(n.Attributes.["value"].Value)

                        match roleName, roleEnded with
                        | Some n, false -> n |> MemberParsing.genderRole gender |> Some
                        | _ -> None
                    )
                    |> Seq.toList

                let instruments =
                    let mainInstrument =
                        detailsDoc.GetElementbyId("mit_hauptinstrument")
                        |> getSelectedOption
                    match mainInstrument with
                    | Some x ->
                        let otherInstruments =
                            detailsDoc.DocumentNode.Descendants("input")
                            |> Seq.filter (fun n ->
                                n.Attributes.["name"].Value.StartsWith("mit_nebeninstrumente[", StringComparison.InvariantCultureIgnoreCase)
                                && n.Attributes.["checked"] <> null
                            )
                            |> Seq.map (fun n -> n.Attributes.["value"].Value)
                            |> Seq.toList
                        x :: otherInstruments
                    | None -> []

                let noneWhenNullOrWhitespace s =
                    if String.IsNullOrWhiteSpace s
                    then None
                    else Some s

                {
                    Member =
                        {
                            OOEBVId = memberId
                            FirstName = firstName
                            LastName = lastName
                            DateOfBirth = getTextBoxValue "mit_geburtsdatum" |> MemberParsing.parseDate
                            City = getTextBoxValue "mit_ort" |> MemberParsing.normalizeName
                            Phone = [ getTextBoxValue "mit_mobil"; getTextBoxValue "mit_telefon1"; getTextBoxValue "mit_telefon2" ] |> MemberParsing.firstNonEmptyOrNone
                            Email = getTextBoxValue "mit_email" |> noneWhenNullOrWhitespace
                            MemberSince = memberSince
                            Roles = roles
                            Instruments = instruments
                        }
                    Image = image
                    IsActive = isActive
                }
            )
        )
    )
    |> Choice.ofList

let loadAllMembers x =
    loadMembers (fun _ -> true) x

let loadActiveMembers x =
    let filter (row: HtmlNode) =
        row.SelectSingleNode("td[1]").InnerText
        |> MemberParsing.isMember
    loadMembers filter x
