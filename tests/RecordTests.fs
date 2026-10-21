module RecordTests

open System
open System.Text
open Xunit
open Swensen.Unquote
open Nocfo.Domain
open Nocfo.Cashbook
open Nocfo.Tools

let private ids = Map [ "1930", 616874; "3010", 616911; "8100", 616914 ]

let private tags = Map [ "flagged", 71; "ambiguous", 72 ]

let private income : Document =
    { date = DateOnly(2026, 7, 18); payment = { number = "1930"; name = "MobilePay" }; offset = { number = "3010"; name = "Tanssit" }
      amount = 44.70M; rows = []; tags = []; description = "[kassakirja] MobilePay/Tanssit 2026-07-18 (5)" }

let private fee =
    { income with offset = { number = "8100"; name = "Palvelumaksut" }; amount = -2M; tags = [ "ambiguous"; "flagged" ]
                  description = "[kassakirja] MobilePay/Palvelumaksut 2026-07-18 (1)" }

/// A document as `GET /document/` lists it, its blueprint as the API gives it back (every field, VAT included).
let private listed (date: string) (description: string) (blueprint: string) : DocumentRow =
    NocfoApi.Http.Serializer.deserialize<DocumentRow>
        $"""{{"id": 72087, "created_at": "2026-07-19T10:00:00+03:00", "updated_at": "2026-07-19T10:00:00+03:00", "is_locked": false,
              "is_draft": true, "date": "{date}", "description": "{description}", "balance": 0.0, "blueprint_type": "SALES",
              "entry_ui_rows": [], "relations": [], "blueprint": {blueprint}}}"""

let private incomeBlueprint =
    """{"debet_account_id": 616874, "debet_entries": [], "credit_account_id": null, "credit_entries": [{"account_id": 616911,
        "description": null, "vat_code": 3, "vat_rate": 0.0, "vat_method": 0, "amount": 44.7, "source_account_number": null}],
        "expense_entries": []}"""

[<Fact>]
let ``An income debits the payment account and credits the offset account (P2)`` () =
    test <@ Record.blueprint ids income
              = { debet_account_id = Some 616874; debet_entries = []; credit_account_id = None
                  credit_entries = [ { account_id = 616911; amount = 44.70M } ] } @>

[<Fact>]
let ``An expense credits the payment account and debits the offset account with the amount unsigned (P2)`` () =
    test <@ Record.blueprint ids fee
              = { debet_account_id = None; debet_entries = [ { account_id = 616914; amount = 2M } ]
                  credit_account_id = Some 616874; credit_entries = [] } @>

[<Fact>]
let ``The payload is a draft dated and described as planned, its listing attached, unflagged without tags (P1, P7, P12)`` () =
    let payload = Record.payload ids tags 5001 income
    test <@ { payload with blueprint = None }
              = { date = Some "2026-07-18"; description = Some income.description; is_draft = Some true; is_flagged = Some false
                  ``type`` = None; blueprint = None; attachment_ids = Some [ 5001 ]; tag_ids = Some [] } @>
    test <@ payload.blueprint.Value.GetRawText()
              = """{"debet_account_id":616874,"debet_entries":[],"credit_entries":[{"account_id":616911,"amount":44.70}]}""" @>

[<Fact>]
let ``A tagged document is flagged and carries the ids of its tags (P7)`` () =
    let payload = Record.payload ids tags 5002 fee
    test <@ (payload.is_flagged, payload.tag_ids) = (Some true, Some [ 72; 71 ]) @>

[<Fact>]
let ``The attachment is the listing as UTF-8 text under the listing's name (P12)`` () =
    let attachment = Record.attachment income
    test <@ (attachment.name, attachment.``type``) = ("[kassakirja] MobilePay-Tanssit 2026-07-18 (5).txt", "text/plain") @>
    test <@ Encoding.UTF8.GetString(Convert.FromBase64String attachment.file) = Listing.render income @>

[<Fact>]
let ``A listed document equal in date, accounts, amount and description is the planned one recorded (P5)`` () =
    test <@ Record.recorded ids income (listed "2026-07-18" income.description incomeBlueprint) @>

[<Fact>]
let ``A listed document differing in any of them is not (P5)`` () =
    test <@ not (Record.recorded ids income (listed "2026-07-17" income.description incomeBlueprint)) @>
    test <@ not (Record.recorded ids income (listed "2026-07-18" fee.description incomeBlueprint)) @>
    test <@ not (Record.recorded ids income (listed "2026-07-18" income.description (incomeBlueprint.Replace("44.7", "44.71")))) @>
    test <@ not (Record.recorded ids income (listed "2026-07-18" income.description (incomeBlueprint.Replace("616911", "616914")))) @>
