﻿using System;
using Robust.Shared.ViewVariables;

namespace Robust.Client.UserInterface.Controls
{
    /// <summary>
    ///     Base class for a generic UI button.
    /// </summary>
    /// <seealso cref="Button"/>
    /// <seealso cref="TextureButton"/>
    /// <seealso cref="CheckBox"/>
    public abstract class BaseButton : Control
    {
        private bool _attemptingPress;
        private bool _beingHovered;
        private bool _disabled;
        private bool _pressed;

        /// <summary>
        ///     Controls mode of operation in relation to press/release events.
        /// </summary>
        [ViewVariables]
        public ActionMode Mode { get; set; } = ActionMode.Release;

        /// <summary>
        ///     Whether the button is disabled.
        ///     If a button is disabled, it appears greyed out and cannot be interacted with.
        /// </summary>
        [ViewVariables]
        public bool Disabled
        {
            get => _disabled;
            set
            {
                var old = _disabled;
                _disabled = value;

                if (old != value)
                {
                    DrawModeChanged();
                }
            }
        }

        /// <summary>
        ///     Whether the button is currently toggled down. Only applies when <see cref="ToggleMode"/> is true.
        /// </summary>
        [ViewVariables]
        public bool Pressed
        {
            get => _pressed;
            set
            {
                if (_pressed == value)
                {
                    return;
                }
                _pressed = value;

                DrawModeChanged();
            }
        }

        /// <summary>
        ///     If <c>true</c>, this button functions as a toggle, not as a regular push button.
        /// </summary>
        [ViewVariables]
        public bool ToggleMode { get; set; }

        /// <summary>
        ///     If <c>true</c>, this button is currently being hovered over by the mouse.
        /// </summary>
        [ViewVariables]
        public bool IsHovered => _beingHovered;

        /// <summary>
        ///     Draw mode used for styling of buttons.
        /// </summary>
        [ViewVariables]
        public DrawModeEnum DrawMode
        {
            get
            {
                if (Disabled)
                {
                    return DrawModeEnum.Disabled;
                }

                if (Pressed || _attemptingPress)
                {
                    return DrawModeEnum.Pressed;
                }

                if (IsHovered)
                {
                    return DrawModeEnum.Hover;
                }

                return DrawModeEnum.Normal;
            }
        }

        /// <summary>
        ///     Fired when the button is pushed down by the mouse.
        /// </summary>
        public event Action<ButtonEventArgs> OnButtonDown;

        /// <summary>
        ///     Fired when the button is released by the mouse.
        /// </summary>
        public event Action<ButtonEventArgs> OnButtonUp;

        /// <summary>
        ///     Fired when the button is "pressed". When this happens depends on <see cref="Mode"/>.
        /// </summary>
        public event Action<ButtonEventArgs> OnPressed;

        /// <summary>
        ///     If <see cref="ToggleMode"/> is set, fired when the button is toggled up or down.
        /// </summary>
        public event Action<ButtonToggledEventArgs> OnToggled;

        protected virtual void DrawModeChanged()
        {
        }

        protected internal override void MouseDown(GUIMouseButtonEventArgs args)
        {
            base.MouseDown(args);

            if (Disabled)
            {
                return;
            }

            var buttonEventArgs = new ButtonEventArgs(this);
            OnButtonDown?.Invoke(buttonEventArgs);

            var drawMode = DrawMode;
            if (Mode == ActionMode.Release)
            {
                _attemptingPress = true;
            }
            else
            {
                if (ToggleMode)
                {
                    _pressed = !_pressed;
                    OnPressed?.Invoke(buttonEventArgs);
                    OnToggled?.Invoke(new ButtonToggledEventArgs(Pressed, this));
                }
                else
                {
                    _attemptingPress = true;
                    OnPressed?.Invoke(buttonEventArgs);
                }
            }

            if (drawMode != DrawMode)
            {
                DrawModeChanged();
            }
        }

        protected internal override void MouseUp(GUIMouseButtonEventArgs args)
        {
            base.MouseUp(args);

            if (Disabled)
            {
                return;
            }

            var buttonEventArgs = new ButtonEventArgs(this);
            OnButtonUp?.Invoke(buttonEventArgs);

            var drawMode = DrawMode;
            if (Mode == ActionMode.Release && _attemptingPress)
            {
                if (ToggleMode)
                {
                    _pressed = !_pressed;
                }

                OnPressed?.Invoke(buttonEventArgs);
                if (ToggleMode)
                {
                    OnToggled?.Invoke(new ButtonToggledEventArgs(Pressed, this));
                }
            }

            _attemptingPress = false;
            if (drawMode != DrawMode)
            {
                DrawModeChanged();
            }
        }

        protected internal override void MouseEntered()
        {
            base.MouseEntered();

            var drawMode = DrawMode;
            _beingHovered = true;
            if (drawMode != DrawMode)
            {
                DrawModeChanged();
            }
        }

        protected internal override void MouseExited()
        {
            base.MouseExited();

            var drawMode = DrawMode;
            _beingHovered = false;
            if (drawMode != DrawMode)
            {
                DrawModeChanged();
            }
        }

        public enum DrawModeEnum
        {
            Normal = 0,
            Pressed = 1,
            Hover = 2,
            Disabled = 3
        }

        public class ButtonEventArgs : EventArgs
        {
            /// <summary>
            ///     The button this event originated from.
            /// </summary>
            public BaseButton Button { get; }

            public ButtonEventArgs(BaseButton button)
            {
                Button = button;
            }
        }

        /// <summary>
        ///     Fired when a <see cref="BaseButton"/> is toggled.
        /// </summary>
        public class ButtonToggledEventArgs : ButtonEventArgs
        {
            /// <summary>
            ///     The new pressed state of the button.
            /// </summary>
            public bool Pressed { get; }

            public ButtonToggledEventArgs(bool pressed, BaseButton button) : base(button)
            {
                Pressed = pressed;
            }
        }

        /// <summary>
        ///     For use with <see cref="BaseButton.Mode"/>.
        /// </summary>
        public enum ActionMode
        {
            /// <summary>
            ///     <see cref="BaseButton.OnPressed"/> fires when the mouse button causing them is pressed down.
            /// </summary>
            Press = 0,

            /// <summary>
            ///     <see cref="BaseButton.OnPressed"/> fires when the mouse button causing them is released.
            ///     This is the default and most intuitive method.
            /// </summary>
            Release = 1
        }
    }
}
