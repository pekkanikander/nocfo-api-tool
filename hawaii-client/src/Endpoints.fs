namespace NocfoClient

open System

module Endpoints =
    let private seg (value: string) = Uri.EscapeDataString value

    let private paged (path: string) (page: int) = $"{path}?page_size=100&page={page}"

    let businessListUrl               = "/business/"
    let businessBySlug (slug: string) = $"/business/{seg slug}/"
    let businessList (page: int)      = paged businessListUrl page

    let accountsBySlug (slug: string) =
        $"/business/{seg slug}/account/"
    let accountsBySlugPage (slug: string) (page: int) =
        paged (accountsBySlug slug) page
    let accountById (slug: string) (id: string) =
        $"/business/{seg slug}/account/{seg id}/"

    let documentsBySlug (slug: string) =
        $"/business/{seg slug}/document/"
    let documentsBySlugPage (slug: string) (page: int) =
        paged (documentsBySlug slug) page
    let documentById (slug: string) (id: string) =
        $"/business/{seg slug}/document/{seg id}/"

    let contactsBySlug (slug: string) =
        $"/business/{seg slug}/contacts/"
    let contactsBySlugPage (slug: string) (page: int) =
        paged (contactsBySlug slug) page
    let contactById (slug: string) (id: string) =
        $"/business/{seg slug}/contacts/{seg id}/"
