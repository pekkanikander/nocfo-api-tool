module DomainDiffTests

open System
open Xunit
open Swensen.Unquote
open NocfoApi.Types
open Nocfo.Domain

// ── Helpers ───────────────────────────────────────────────────────────────────

// Minimal AccountFull using the generated static Create factory (all optionals default to None).
let private makeAccount (id: int) (name: string) : AccountFull =
    Account.Create(
        id              = id,
        created_at      = DateTimeOffset.UtcNow,
        updated_at      = DateTimeOffset.UtcNow,
        number          = "1000",
        padded_number   = 1000,
        name            = name,
        name_translations = [],
        header_path     = [],
        default_vat_rate = 0.0,
        is_shown        = true,
        balance         = 0.0f,
        is_used         = false
    )

// PatchedAccountRequest with all fields None, via the generated factory.
let private emptyPatch : PatchedAccountRequest = PatchedAccountRequest.Create()

// ── Account.diffAccount ───────────────────────────────────────────────────────

[<Fact>]
let ``diffAccount: same id, empty patch → Ok None (no changes)`` () =
    let full  = makeAccount 1 "Revenue"
    let delta : AccountDelta = { id = 1; patch = emptyPatch }
    match Account.diffAccount full delta with
    | Ok None -> ()
    | other -> Assert.Fail $"Expected Ok None, got %A{other}"

[<Fact>]
let ``diffAccount: same id, patch matches existing number → Ok None (normalized away)`` () =
    let full  = makeAccount 1 "Revenue"
    // number "1000" matches what makeAccount set → normalize strips it → no changes
    let patch = { emptyPatch with number = Some "1000" }
    let delta : AccountDelta = { id = 1; patch = patch }
    match Account.diffAccount full delta with
    | Ok None -> ()
    | other -> Assert.Fail $"Expected Ok None, got %A{other}"

[<Fact>]
let ``diffAccount: same id, changed number → Ok (Some (UpdateAccount ...))`` () =
    let full  = makeAccount 1 "Revenue"
    let patch = { emptyPatch with number = Some "9999" }
    let delta : AccountDelta = { id = 1; patch = patch }
    // AccountCommand contains a Hydratable (which has a function field) so equality
    // is not available — use pattern matching instead.
    match Account.diffAccount full delta with
    | Ok (Some (UpdateAccount { id = id })) ->
        test <@ id = 1 @>
    | other ->
        Assert.Fail $"Expected Ok (Some (UpdateAccount ...)), got %A{other}"

[<Fact>]
let ``diffAccount: mismatched ids → Error (Unexpected)`` () =
    let full  = makeAccount 1 "Revenue"
    let delta : AccountDelta = { id = 99; patch = emptyPatch }
    match Account.diffAccount full delta with
    | Error (DomainError.Unexpected _) -> ()
    | other -> Assert.Fail $"Expected Error (Unexpected ...), got %A{other}"

// ── Account.classify ──────────────────────────────────────────────────────────
// Note: Account.classify is an inline SRTP function whose member access uses the
// reserved-word field ``type``. Unquote cannot invoke inline functions dynamically,
// so we pre-compute the result and assert on the plain value.

let private classifyAcc (t: Type92dEnum option) =
    Account.classify { makeAccount 1 "?" with ``type`` = t }

[<Fact>]
let ``classify None → None`` () =
    let result = classifyAcc None
    test <@ result = None @>

[<Fact>]
let ``classify ASS → Asset`` () =
    let result = classifyAcc (Some Type92dEnum.ASS)
    test <@ result = Some Asset @>

[<Fact>]
let ``classify ASS_PAY → Asset`` () =
    let result = classifyAcc (Some Type92dEnum.ASS_PAY)
    test <@ result = Some Asset @>

[<Fact>]
let ``classify LIA → Liability`` () =
    let result = classifyAcc (Some Type92dEnum.LIA)
    test <@ result = Some Liability @>

[<Fact>]
let ``classify LIA_VAT → Liability`` () =
    let result = classifyAcc (Some Type92dEnum.LIA_VAT)
    test <@ result = Some Liability @>

[<Fact>]
let ``classify REV → Income`` () =
    let result = classifyAcc (Some Type92dEnum.REV)
    test <@ result = Some Income @>

[<Fact>]
let ``classify REV_NO → Income`` () =
    let result = classifyAcc (Some Type92dEnum.REV_NO)
    test <@ result = Some Income @>

[<Fact>]
let ``classify EXP → Expense`` () =
    let result = classifyAcc (Some Type92dEnum.EXP)
    test <@ result = Some Expense @>

[<Fact>]
let ``classify EXP_TAX_PRE → Expense`` () =
    let result = classifyAcc (Some Type92dEnum.EXP_TAX_PRE)
    test <@ result = Some Expense @>

// ── Business construction ─────────────────────────────────────────────────────

let private makeBusiness (slug: string option) (identifiers: BusinessIdentifier list) : NocfoApi.Types.Business =
    { NocfoApi.Types.Business.Create(
        id = 1,
        created_at = DateTimeOffset.UtcNow,
        updated_at = DateTimeOffset.UtcNow,
        logo = None,
        name = "Acme Oy",
        country = "FI",
        country_config = Nocfo.JsonHelpers.jsonString "{}",
        period_id = None,
        is_billable = true,
        vat_period_id = None,
        owner_name = "Owner",
        owner_email = "owner@example.test",
        invoicing_tax_code = None,
        has_business_address = false,
        subscription_plan = "free",
        subscription_source = "none",
        stripe_customer_id = None,
        stripe_subscription_id = None,
        stripe_plan_status = None,
        stripe_billing_interval = None,
        can_invoice = false,
        einvoicing_address = None,
        einvoicing_operator = None,
        apix_unique_id = None,
        apix_transfer_id = None,
        apix_transfer_key = None,
        salaxy_enabled = false,
        salaxy_account_id = None,
        demo_days_left = 0,
        is_eligible_for_einvoicing = false,
        has_tags = false,
        identifiers = identifiers,
        is_new_invoicer = false) with
        slug = slug }

[<Fact>]
let ``A business without identifiers is listed rather than crashing the stream`` () =
    // `identifiers` is required by the spec but may be empty; indexing [0] used to abort the
    // whole `list businesses` run.
    let result = Business.ofRaw (makeBusiness (Some "acme") [])
    match result with
    | Hydratable.Full full -> test <@ full.key.slug = "acme" @>
    | Hydratable.Partial _ -> Assert.Fail "expected a Full business"

[<Fact>]
let ``A business without a slug falls back to a slug no endpoint matches`` () =
    let result = Business.ofRaw (makeBusiness None [])
    match result with
    | Hydratable.Full full -> test <@ full.key.slug = Business.UnknownSlug @>
    | Hydratable.Partial _ -> Assert.Fail "expected a Full business"
