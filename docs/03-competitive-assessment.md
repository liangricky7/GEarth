# Crawl: competitive assessment and decision gate

Research checked October 9, 2026. Product descriptions below are based on their own public pages; they are not hands-on feature tests.

## What problem is supported by the survey?

The 57 responses indicate difficulty reaching agreement and coordinating a group. Reported abandonment because of travel time (56%) and frequently finding places that are too far (46%) support a reachability problem. Cost ranked first and travel time close behind as place-choice factors. This is evidence of a problem, not evidence that people will use Crawl or that its features outperform existing tools.

## Existing options

| Product | Publicly described overlap with Crawl | What remains unclear from public material |
| --- | --- | --- |
| [Google Maps group planning announcement](https://blog.google/products-and-platforms/products/maps/all-together-now-group-planning-google-maps/) | Shared place shortlist and voting announced in 2018 | Whether that historical group-planning flow is still available in the same form today; no group reach-zone claim in the announcement |
| [MeetWays](https://www.meetways.com/) | Two-address halfway search, points of interest, directions and reviews | Group constraints beyond two starting addresses |
| [Halfway](https://halfway.place/) | Travel-time overlap and recommended venues; driving and transit; shareable location request | Voting and individual budgets |
| [Meetway](https://meetway.ca/) | Shared plan and invite, travel areas, highlighted shared area, time limit, recommended spots, voting | Individual time caps and price constraints are not shown on its public page |
| [Mimble](https://mimbl3.com/how-it-works/) | Group travel times, preference and price-range filters, AI-assisted search, venue explanation, live voting | Explicit reach-zone intersection and hard per-person time or dollar caps are not shown in its guide. Its [support page](https://mimbl3.com/support/) says live voters need its iPhone app. |

The original claim that no existing map app addresses whole-group reachability is false. Meetway is especially close to Crawl's proposed demo. AI selection, voting, price ranges, and travel-time fairness already exist in Mimble's described flow.

## Stronger Crawl proposition to test

“For a spontaneous outing, each person sets their own maximum travel time and price ceiling. Crawl displays the shared feasible area, shows only candidates that meet every person's limits, explains the tradeoffs, and gets an immediate group decision from any phone.”

This is a hypothesis, not a proven market gap. Individual hard constraints and a no-install flow need direct comparison with the alternatives. Exact dollar affordability also needs reliable prices; a Places price level alone cannot support an exact ceiling.

## Go / change / stop check before full build

1. Ask three to five groups to plan a real outing using their usual process and at least one competitor. Observe time to a choice, workarounds, and points where someone opts out.
2. Show a clickable Crawl prototype for the same scenario. Test whether individual limits and the overlap visibly change the decision. Ask what they would use next time, then watch whether they actually share it with their group.
3. Proceed with the current demo if groups repeatedly choose a place faster or avoid a travel/price conflict that the alternatives missed. Narrow the scope to the feature causing that improvement. If the same decision is easy in Meetway or Mimble, reconsider the concept rather than relying on the map overlay or local AI as the distinction.

For a hackathon, a complete working loop with a clear decision outcome can still be worthwhile. As a longer-term product, the broad “group meetup map with AI and voting” framing is already crowded; its distinct value remains to be demonstrated.
