# Crawl: 36-hour demo plan

## Core promise

Every returned place is inside the group reach-zone overlap and is checked against each member's travel-time limit. A place is called affordable only when its price evidence supports that claim. The AI picks from those candidates; it cannot add new ones.

## Demo loop

1. A host creates a group session and shares a join link or code.
2. Each member supplies a start location, travel mode, maximum travel time, and budget or price preference.
3. Generate each member's travel-time polygon and show it on the Google map. Compute and highlight the intersection. Solo mode shows one polygon.
4. Search for places around the overlap, then keep only results inside the intersection. Apply price rules and check finalists' per-member route times.
5. Send a small shortlist with place IDs, categories, price evidence, ratings, and each member's travel time to the local model. The model returns one eligible ID and a short reason.
6. Display the suggestion and let members vote. Show the vote result.

## Important boundaries

- [Google's Isochrones API](https://developers.google.com/maps/documentation/isochrones/overview) provides road-network travel-time polygons. Its [current modes and limits](https://developers.google.com/maps/documentation/isochrones/key-concepts) cover driving or two-wheelers up to 60 minutes and walking or cycling up to 120 minutes. Transit is not listed as a supported mode.
- Use the raw, unsmoothed polygon for containment and overlap. Smoothing changes polygon boundaries; a smoothed copy can be used for visual display only. See [Google's Isochrones FAQ](https://developers.google.com/maps/documentation/isochrones/faq).
- [Places Text Search](https://developers.google.com/maps/documentation/places/web-service/reference/rest/v1/places/searchText) can restrict results to a viewport, not an arbitrary group polygon. Search a bounding area and apply your own point-in-polygon filter.
- A Places price level is a rough category. An exact per-person dollar cap requires a known ticket, menu, or other dependable price. Mark missing prices as unknown instead of asserting affordability. See [Google Place price fields](https://developers.google.com/maps/documentation/places/web-service/reference/rest/v1/places).
- Confirm shortlisted candidates with the [Routes API route matrix](https://developers.google.com/maps/documentation/routes/) so the map preview does not become the sole proof of reachability.

## Suggested acceptance check

Run one scripted group with at least two different origins. A place outside either person's time limit never appears in search or the AI suggestion. Changing a member's limit changes the overlap and candidate list. A vote updates for every participant. Run one solo case to confirm that only one zone appears.
