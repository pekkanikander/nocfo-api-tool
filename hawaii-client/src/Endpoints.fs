namespace NocfoClient

module Endpoints =
    let businessListUrl               = "/business/"
    let businessBySlug (slug: string) = $"/business/{slug}/"
    let businessList (page: int)      = $"/business/?page_size=100&page={page}"

    let accountsBySlug (slug: string) =
        $"/business/{slug}/account/"
    let accountsBySlugPage (slug: string) (page: int) =
        $"/business/{slug}/account/?page_size=100&page={page}"
    let accountById (slug: string) (id: string) =
        $"/business/{slug}/account/{id}/"

    let documentsBySlugPage (slug: string) (page: int) =
        $"/business/{slug}/document/?page_size=100&page={page}"
    let documentsBySlug (slug: string) =
        $"/business/{slug}/document/"
    let documentById (slug: string) (id: string) =
        $"/business/{slug}/document/{id}/"

    let contactsBySlug (slug: string) =
        $"/business/{slug}/contacts/"
    let contactsBySlugPage (slug: string) (page: int) =
        $"/business/{slug}/contacts/?page_size=100&page={page}"
    let contactById (slug: string) (id: string) =
        $"/business/{slug}/contacts/{id}/"
