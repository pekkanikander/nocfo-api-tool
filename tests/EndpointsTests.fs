module EndpointsTests

open Xunit
open Swensen.Unquote
open NocfoClient

[<Fact>]
let ``Path segments are percent-encoded`` () =
    let result = Endpoints.businessBySlug "a/b?c#d"
    test <@ result = "/business/a%2Fb%3Fc%23d/" @>

[<Fact>]
let ``Identifiers are percent-encoded`` () =
    let result = Endpoints.accountById "acme" "1/2"
    test <@ result = "/business/acme/account/1%2F2/" @>

[<Fact>]
let ``Ordinary slugs are unchanged`` () =
    let result = Endpoints.contactsBySlug "acme-oy"
    test <@ result = "/business/acme-oy/contacts/" @>

[<Fact>]
let ``Paged endpoints append the paging query once`` () =
    let result = Endpoints.documentsBySlugPage "acme" 3
    test <@ result = "/business/acme/document/?page_size=100&page=3" @>

[<Fact>]
let ``Business list paging matches the list url`` () =
    let result = Endpoints.businessList 1
    test <@ result = Endpoints.businessListUrl + "?page_size=100&page=1" @>
