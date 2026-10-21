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

[<Fact>]
let ``Dated document pages append the date range to the paging query`` () =
    let result = Endpoints.documentsBySlugDatedPage "acme" "2026-07-18" "2026-07-20" 2
    test <@ result = "/business/acme/document/?page_size=100&page=2&date_from=2026-07-18&date_to=2026-07-20" @>

[<Fact>]
let ``File uploads are posted under the business`` () =
    let result = Endpoints.fileUpload "acme"
    test <@ result = "/business/acme/file_upload/" @>

[<Fact>]
let ``Files are listed and addressed under the business`` () =
    let page = Endpoints.filesBySlugPage "acme" 1
    let file = Endpoints.fileById "acme" "42"
    test <@ (page, file) = ("/business/acme/files/?page_size=100&page=1", "/business/acme/files/42/") @>
