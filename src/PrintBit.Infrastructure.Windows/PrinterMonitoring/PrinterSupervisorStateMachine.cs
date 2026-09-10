using System;
using System.Collections.Generic;
using PrintBit.Infrastructure.Services.PrintService;
using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Windows.PrinterMonitoring;

public enum SupervisorDecision
{
    None,
    StartSpooler,
    RestartSpooler
}

public sealed record SupervisorObservation(
    SpoolerStatusSnapshot Spooler,
    PrinterHealthDiagnostic Printer,
    PrinterOperationKind ActiveOperation);

public sealed record SupervisorTransition(
    PrinterSupervisorState State,
    SupervisorDecision Decision,
    bool StateChanged,
    int AttemptsInWindow);

public sealed class PrinterSupervisorStateMachine
{
    private readonly PrinterRecoverySettings _settings;
    private readonly ISystemClock _clock;
    private readonly List<DateTime> _failedRecoveryAttempts = [];
    private int _healthySamples;
    private int _unhealthySamples;

    public PrinterSupervisorStateMachine(PrinterRecoverySettings settings, ISystemClock clock)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        if (!_settings.IsValidSupervisorPolicy())
        {
            throw new ArgumentException("Printer supervisor policy settings must be positive.", nameof(settings));
        }
    }

    public PrinterSupervisorState State { get; private set; } = PrinterSupervisorState.Starting;

    public SupervisorTransition Observe(SupervisorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Spooler);
        ArgumentNullException.ThrowIfNull(observation.Printer);

        PruneFailedRecoveryAttempts();

        if (observation.Printer.IssueKind == PrinterHealthIssueKind.PhysicalFault)
        {
            _healthySamples = 0;
            _unhealthySamples = 0;
            return TransitionTo(PrinterSupervisorState.Maintenance);
        }

        if (IsHealthy(observation))
        {
            return ObserveHealthy(observation.ActiveOperation);
        }

        return ObserveUnhealthy(observation);
    }

    public SupervisorTransition CompleteRecovery(bool succeeded)
    {
        PruneFailedRecoveryAttempts();

        if (State != PrinterSupervisorState.Recovering)
        {
            return CurrentTransition();
        }

        _unhealthySamples = 0;

        if (succeeded)
        {
            _failedRecoveryAttempts.Clear();
            _healthySamples = 1;
            return TransitionTo(PrinterSupervisorState.Starting);
        }

        _healthySamples = 0;
        _failedRecoveryAttempts.Add(_clock.UtcNow);
        PruneFailedRecoveryAttempts();

        return _failedRecoveryAttempts.Count >= _settings.CircuitBreakerFailureLimit
            ? TransitionTo(PrinterSupervisorState.CircuitOpen)
            : TransitionTo(PrinterSupervisorState.Starting);
    }

    public bool BeginManualHalfOpen(PrinterOperationKind activeOperation)
    {
        if (State != PrinterSupervisorState.CircuitOpen || activeOperation != PrinterOperationKind.None)
        {
            return false;
        }

        State = PrinterSupervisorState.Recovering;
        _healthySamples = 0;
        _unhealthySamples = 0;
        return true;
    }

    private SupervisorTransition ObserveHealthy(PrinterOperationKind activeOperation)
    {
        _unhealthySamples = 0;

        if (State == PrinterSupervisorState.CircuitOpen)
        {
            return CurrentTransition();
        }

        if (activeOperation != PrinterOperationKind.None)
        {
            return TransitionTo(activeOperation == PrinterOperationKind.Print
                ? PrinterSupervisorState.Busy
                : PrinterSupervisorState.Recovering);
        }

        if (State == PrinterSupervisorState.Recovering)
        {
            return CurrentTransition();
        }

        _healthySamples++;
        return _healthySamples >= _settings.HealthySamplesBeforeReady
            ? TransitionTo(PrinterSupervisorState.Ready)
            : CurrentTransition();
    }

    private SupervisorTransition ObserveUnhealthy(SupervisorObservation observation)
    {
        _healthySamples = 0;

        if (State == PrinterSupervisorState.CircuitOpen || State == PrinterSupervisorState.Recovering)
        {
            return CurrentTransition();
        }

        _unhealthySamples++;

        if (observation.ActiveOperation == PrinterOperationKind.Print)
        {
            return TransitionTo(PrinterSupervisorState.Busy);
        }

        if (observation.ActiveOperation == PrinterOperationKind.Recovery)
        {
            return TransitionTo(PrinterSupervisorState.Recovering);
        }

        if (_unhealthySamples < _settings.UnhealthySamplesBeforeRecovery)
        {
            return CurrentTransition();
        }

        var decision = observation.Spooler.IsRunning
            ? SupervisorDecision.RestartSpooler
            : SupervisorDecision.StartSpooler;
        return TransitionTo(PrinterSupervisorState.Recovering, decision);
    }

    private bool IsHealthy(SupervisorObservation observation) =>
        observation.Spooler.IsRunning && observation.Printer.IsHealthy;

    private SupervisorTransition TransitionTo(
        PrinterSupervisorState nextState,
        SupervisorDecision decision = SupervisorDecision.None)
    {
        var stateChanged = State != nextState;
        State = nextState;
        return new SupervisorTransition(State, decision, stateChanged, _failedRecoveryAttempts.Count);
    }

    private SupervisorTransition CurrentTransition() =>
        new(State, SupervisorDecision.None, false, _failedRecoveryAttempts.Count);

    private void PruneFailedRecoveryAttempts()
    {
        var cutoff = _clock.UtcNow.AddMinutes(-_settings.CircuitBreakerWindowMinutes);
        _failedRecoveryAttempts.RemoveAll(attempt => attempt <= cutoff);
    }
}
