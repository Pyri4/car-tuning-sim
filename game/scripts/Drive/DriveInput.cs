using System;
using Godot;

namespace CarTuningSim.Drive;

/// <summary>Driving controls, registered at runtime so they need no project-file edits: keyboard plus a gamepad.</summary>
public static class DriveInput
{
    public const string Throttle = "drive_throttle", Brake = "drive_brake", Left = "drive_left", Right = "drive_right";
    public const string ShiftUp = "drive_shift_up", ShiftDown = "drive_shift_down", Handbrake = "drive_handbrake";
    public const string Starter = "drive_starter", Reset = "drive_reset", Autopilot = "drive_autopilot";
    public const string Camera = "drive_camera", Help = "drive_help", Exit = "drive_exit";

    /// <summary>Stick/trigger dead zone below which the gamepad is considered idle.</summary>
    public const float DeadZone = 0.06f;

    public static void EnsureActions()
    {
        Add(Throttle, Keys(Key.W, Key.Up));
        Add(Brake, Keys(Key.S, Key.Down));
        Add(Left, Keys(Key.A, Key.Left));
        Add(Right, Keys(Key.D, Key.Right));
        Add(ShiftUp, Keys(Key.E, Key.Shift), Button(JoyButton.RightShoulder));
        Add(ShiftDown, Keys(Key.Q, Key.Ctrl), Button(JoyButton.LeftShoulder));
        Add(Handbrake, Keys(Key.Space), Button(JoyButton.A));
        Add(Starter, Keys(Key.T), Button(JoyButton.Y));
        Add(Reset, Keys(Key.R), Button(JoyButton.Back));
        Add(Autopilot, Keys(Key.P));
        Add(Camera, Keys(Key.C), Button(JoyButton.X));
        Add(Help, Keys(Key.H));
        Add(Exit, Keys(Key.Escape), Button(JoyButton.Start));
    }

    /// <summary>Analogue inputs from the first gamepad, or null when none is connected or it is idle.</summary>
    public static (double Throttle, double Brake, double Steer)? ReadJoypad()
    {
        if (Input.GetConnectedJoypads().Count == 0) return null;
        float throttle = Input.GetJoyAxis(0, JoyAxis.TriggerRight);
        float brake = Input.GetJoyAxis(0, JoyAxis.TriggerLeft);
        float steer = -Input.GetJoyAxis(0, JoyAxis.LeftX);
        if (throttle < DeadZone && brake < DeadZone && Math.Abs(steer) < DeadZone) return null;
        static double Dz(float v) => Math.Abs(v) < DeadZone ? 0 : (v - Math.Sign(v) * DeadZone) / (1 - DeadZone);
        return (Dz(throttle), Dz(brake), Dz(steer));
    }

    private static InputEvent[] Keys(params Key[] keys) => Array.ConvertAll(keys, k => (InputEvent)new InputEventKey { PhysicalKeycode = k });

    private static InputEvent[] Button(JoyButton b) => new InputEvent[] { new InputEventJoypadButton { ButtonIndex = b } };

    private static void Add(string action, params InputEvent[][] events)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        foreach (var group in events)
            foreach (var e in group) InputMap.ActionAddEvent(action, e);
    }
}
