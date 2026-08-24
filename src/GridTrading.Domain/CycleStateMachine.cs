namespace GridTrading.Domain;

public static class CycleStateMachine
{
    public static bool CanTransition(CycleState current, CycleState next) => (current, next) switch
    {
        (CycleState.WaitingForOperator, CycleState.Starting) => true,
        (CycleState.Starting, CycleState.Running or CycleState.Closing or CycleState.Fault) => true,
        (CycleState.Running, CycleState.Paused or CycleState.Closing or CycleState.Fault) => true,
        (CycleState.Paused, CycleState.Running or CycleState.Closing or CycleState.Fault) => true,
        (CycleState.Closing, CycleState.WaitingForOperator or CycleState.Fault) => true,
        (CycleState.Fault, CycleState.Closing) => true,
        _ => false
    };

    public static IReadOnlyList<string> AllowedCommands(CycleState state, bool hasExposure) => state switch
    {
        CycleState.Running => ["PAUSE_ENTRIES", "CLOSE", "EMERGENCY_FLATTEN", "RECONCILE"],
        CycleState.Paused => ["RESUME_ENTRIES", "CLOSE", "EMERGENCY_FLATTEN", "RECONCILE"],
        CycleState.Starting or CycleState.Closing => hasExposure ? ["EMERGENCY_FLATTEN", "RECONCILE"] : ["RECONCILE"],
        CycleState.Fault => hasExposure ? ["EMERGENCY_FLATTEN", "RECONCILE"] : ["RECONCILE"],
        _ => []
    };

    public static void EnsureTransition(CycleState current, CycleState next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"Invalid cycle state transition: {current} -> {next}.");
    }
}
