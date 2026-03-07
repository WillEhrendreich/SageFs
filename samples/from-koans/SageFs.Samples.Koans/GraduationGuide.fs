module SageFs.Samples.Koans.GraduationGuide

open Expecto
open Expecto.Flip

type OrderStatus =
  | Pending
  | Shipped of trackingNumber: string
  | Delivered of deliveredAt: System.DateTime
  | Cancelled of reason: string

let describeOrder status =
  match status with
  | Pending -> "⏳ Awaiting shipment"
  | Shipped t -> sprintf "📦 Shipped — tracking: %s" t
  | Delivered d -> sprintf "✅ Delivered on %s" (d.ToString("yyyy-MM-dd"))
  | Cancelled r -> sprintf "❌ Cancelled: %s" r

type Sale = { Product: string; Amount: float; Region: string }

let sales = [
  { Product = "Widget"; Amount = 29.99; Region = "North" }
  { Product = "Gadget"; Amount = 49.99; Region = "South" }
  { Product = "Widget"; Amount = 29.99; Region = "South" }
  { Product = "Gizmo"; Amount = 99.99; Region = "North" }
  { Product = "Widget"; Amount = 29.99; Region = "North" }
  { Product = "Gadget"; Amount = 49.99; Region = "North" }
]

let revenueByRegion =
  sales
  |> List.groupBy (fun s -> s.Region)
  |> List.map (fun (region, items) ->
    region, items |> List.sumBy (fun s -> s.Amount))

let topProduct =
  sales
  |> List.countBy (fun s -> s.Product)
  |> List.sortByDescending snd
  |> List.head

let orderTests = testList "order status" [
  test "pending orders show waiting message" {
    let msg = describeOrder Pending
    msg |> Expect.stringContains "should mention awaiting" "Awaiting"
  }
  test "shipped orders include tracking number" {
    let msg = describeOrder (Shipped "ABC123")
    msg |> Expect.stringContains "should contain tracking" "ABC123"
  }
  test "cancelled orders explain why" {
    let msg = describeOrder (Cancelled "Out of stock")
    msg |> Expect.stringContains "should contain reason" "Out of stock"
  }
]

let pipelineTests = testList "pipeline exercises" [
  test "revenue by region sums correctly" {
    let northRevenue =
      revenueByRegion
      |> List.find (fun (r, _) -> r = "North")
      |> snd
    northRevenue |> Expect.floatClose "north revenue" Accuracy.medium 209.96
  }
  test "top product is Widget" {
    (fst topProduct) |> Expect.equal "most frequent product" "Widget"
  }
]

let tests = testList "graduation guide" [
  orderTests
  pipelineTests
]
