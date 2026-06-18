using System;

namespace Fluentometer.Logic.Ui;

public interface IUiDispatcher
{
    void Post(Action action);
}
