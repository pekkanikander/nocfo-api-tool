module DomainDiffTests

open System
open Xunit
open Swensen.Unquote
open NocfoApi.Types
open Nocfo.Domain

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
