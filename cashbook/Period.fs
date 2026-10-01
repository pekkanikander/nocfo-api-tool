namespace Nocfo.Cashbook

open System

/// An inclusive range of dates; a bound left out is open.
type Period =
    { from_date: DateOnly option
      to_date:   DateOnly option }

module Period =

    let contains (period: Period) (date: DateOnly) =
        Option.forall (fun from -> date >= from) period.from_date
        && Option.forall (fun until -> date <= until) period.to_date
