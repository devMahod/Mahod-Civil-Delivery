using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Core.Tests.Estimate;

public sealed class NamelessDecisionUiTests
{
    [Fact]
    public void Removing_identity_entry_keeps_actual_user_decisions_unchecked()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var field = new TextBox(); var label = new TextBlock { Text = "מאשר:" };
                var choice = new CheckBox { IsChecked = false }; var button = new Button { Content = "שמור" };
                var body = new StackPanel(); body.Children.Add(label); body.Children.Add(field); body.Children.Add(choice); body.Children.Add(button);
                var window = new Window { Content = body };
                NamelessDecisionField.Attach(window, field);
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal("user-choice:no-name", field.Text);
                Assert.Equal(Visibility.Collapsed, field.Visibility); Assert.Equal(Visibility.Collapsed, label.Visibility);
                Assert.Equal(Visibility.Visible, button.Visibility); Assert.False(choice.IsChecked);
                Assert.Equal("user-choice:no-name", new ApproverContext().Require("map", (_, _) => throw new Exception("No identity dialog should open")));
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
