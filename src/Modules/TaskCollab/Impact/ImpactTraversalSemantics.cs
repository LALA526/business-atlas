namespace TaskCollab.Impact;

/// <summary>
/// Directional relations that can propagate a change investigation. Scope, provenance and
/// generic dependency relations deliberately do not appear here: they are review clues,
/// not evidence that a neighbour is impacted.
/// </summary>
public static class ImpactTraversalSemantics
{
    public static readonly IReadOnlyList<string> Predicates =
    [
        "defines", "specializes", "refines", "usesFact", "constrains", "performs",
        "triggers", "followsWhen", "changesState", "satisfies", "implements", "mapsTo",
        "calls", "reads", "writes", "governedBy", "validates"
    ];

    public static bool Allows(string kind, string predicate, string direction) => kind switch
    {
        "DataField" or "DataEntity" =>
            predicate == "mapsTo" && direction == "reverse" ||
            predicate is "reads" or "writes" && direction == "reverse",

        "BusinessAttribute" or "DomainConcept" or "BusinessObject" =>
            predicate == "mapsTo" && direction == "forward" ||
            predicate is "usesFact" or "reads" or "writes" && direction == "reverse" ||
            predicate is "defines" or "specializes" && direction is "forward" or "reverse" ||
            predicate == "governedBy" && direction == "forward",

        "BusinessRule" or "Rule" =>
            predicate == "usesFact" && direction == "forward" ||
            predicate is "constrains" or "refines" or "governedBy" && direction == "forward" ||
            predicate is "implements" or "validates" && direction == "reverse" ||
            predicate == "refines" && direction == "reverse" ||
            predicate == "specializes" && direction is "forward" or "reverse",

        "Behavior" or "Action" =>
            predicate is "constrains" or "implements" or "performs" && direction == "reverse" ||
            predicate is "satisfies" or "reads" or "writes" or "changesState" or "governedBy" && direction == "forward" ||
            predicate is "triggers" or "followsWhen" && direction is "forward" or "reverse",

        "Requirement" or "QualityRequirement" =>
            predicate is "satisfies" or "implements" or "validates" && direction == "reverse" ||
            predicate is "refines" or "governedBy" && direction == "forward" ||
            predicate == "refines" && direction == "reverse",

        "Implementation" or "Api" or "Component" or "Operation" or "Module" =>
            predicate is "implements" or "mapsTo" or "constrains" && direction is "forward" or "reverse" &&
                (predicate != "mapsTo" || direction == "reverse") &&
                (predicate != "implements" || direction == "forward") &&
                (predicate != "constrains" || direction == "reverse") ||
            predicate is "calls" && direction is "forward" or "reverse" ||
            predicate is "reads" or "writes" or "governedBy" or "performs" && direction == "forward",

        "BusinessCase" or "Test" => predicate == "validates" && direction == "forward",

        "Event" or "Transition" or "State" or "Flow" =>
            predicate is "constrains" && direction == "reverse" ||
            predicate is "triggers" or "followsWhen" or "changesState" && direction is "forward" or "reverse" ||
            predicate == "governedBy" && direction == "forward",

        _ => false
    };

    public static bool NeedsImplementation(string kind) => kind is "BusinessRule" or "Rule" or "Behavior" or "Action" or "Requirement" or "QualityRequirement";
}
