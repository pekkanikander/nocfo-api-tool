/// The NoCFO document a planned cash-book document becomes (P2, P7, P12), and whether one like it is already
/// recorded (P5).
module Nocfo.Tools.Record

open System
open System.Globalization
open System.Text
open System.Text.Json
open Nocfo.Domain
open Nocfo.Cashbook

let day (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

/// P2: the payment account takes the side of its change, the one entry on the offset account the other side.
/// `ids` are the business's account ids by number (P3).
let blueprint (ids: Map<string, int>) (document: Document) : Blueprint =
    let entries = [ { account_id = ids.[document.offset.number]; amount = abs document.amount } ]
    if document.amount >= 0M then
        { debet_account_id = Some ids.[document.payment.number]; debet_entries = []
          credit_account_id = None; credit_entries = entries }
    else
        { debet_account_id = None; debet_entries = entries
          credit_account_id = Some ids.[document.payment.number]; credit_entries = [] }

/// P12: the listing as `POST /file_upload/` takes it.
let attachment (document: Document) : FileUploadPayload =
    { name     = Listing.name document
      ``type`` = "text/plain"
      file     = Convert.ToBase64String(Encoding.UTF8.GetBytes(Listing.render document)) }

/// The document to create: a draft (P1), flagged and tagged by the ids of its reason tags when it has any (P7),
/// the uploaded listing attached (P12).
let payload (ids: Map<string, int>) (tags: Map<string, int>) (attachmentId: int) (document: Document) : DocumentCreatePayload =
    { date           = Some (day document.date)
      description    = Some document.description
      is_draft       = Some true
      is_flagged     = Some (not document.tags.IsEmpty)
      ``type``       = None
      blueprint      = Some (JsonSerializer.SerializeToElement(blueprint ids document, NocfoApi.Http.Serializer.options))
      attachment_ids = Some [ attachmentId ]
      tag_ids        = Some [ for tag in document.tags -> tags.[tag] ] }

/// P5: whether a document already in NoCFO equals the planned one in date, payment account, offset account,
/// amount and description.
let recorded (ids: Map<string, int>) (document: Document) (existing: DocumentRow) : bool =
    let blueprintOf (element: JsonElement) = JsonSerializer.Deserialize<Blueprint>(element, NocfoApi.Http.Serializer.options)
    existing.date = Some (day document.date)
    && existing.description = Some document.description
    && Option.map blueprintOf existing.blueprint = Some (blueprint ids document)
