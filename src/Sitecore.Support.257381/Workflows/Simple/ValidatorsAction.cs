#region using

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using Sitecore.Data.Items;
using Sitecore.Data.Validators;
using Sitecore.Diagnostics;
using Sitecore.Text;
using Sitecore.Web;
using Sitecore.Web.UI.Sheer;
using Sitecore.Web.UI.WebControls;
using Sitecore.Workflows.Simple;

#endregion

namespace Sitecore.Support.Workflows.Simple
{

  /// <summary>
  /// Workflow action that runs all validators on an item and ensures that the current error level
  /// does not exceed a specified max level.
  /// </summary>
  public class ValidatorsAction
  {
    #region Public methods

    /// <summary>
    /// Runs the processor.
    /// </summary>
    /// <param name="args">The arguments.</param>
    public void Process([NotNull] WorkflowPipelineArgs args)
    {
      Assert.ArgumentNotNull(args, "args");

      var item = args.ProcessorItem;
      if (item == null)
      {
        return;
      }

      var commandItem = item.InnerItem;

      var maxResult = GetMaxResult(args, commandItem);
      if (maxResult == ValidatorResult.Unknown)
      {
        return;
      }

      var validators = ValidatorManager.BuildValidators(ValidatorsMode.Workflow, args.DataItem);
      if (validators.Count == 0)
      {
        return;
      }

      var options = new ValidatorOptions(true);

      ValidatorManager.Validate(validators, options);

      var result = Validate(validators, args);

      if (result == ValidatorResult.Valid || result == ValidatorResult.Unknown)
      {
        return;
      }

      if (result <= maxResult)
      {
        return;
      }

      var field = string.Empty;

      switch (result)
      {
        case ValidatorResult.Unknown:
          field = "Unknown";
          break;
        case ValidatorResult.Warning:
          field = "Warning";
          break;
        case ValidatorResult.Error:
          field = "Error";
          break;
        case ValidatorResult.CriticalError:
          field = "Critical Error";
          break;
        case ValidatorResult.FatalError:
          field = "Fatal Error";
          break;
      }

      if (!string.IsNullOrEmpty(field))
      {
        var text = GetText(commandItem, field, args);

        // Only display UI if UI is allowed (not being involved programmatically)
        if (!Context.IsBackgroundThread && (Context.ClientPage.Initialized || AjaxScriptManager.Current != null))
        {
          IFormatter formatter = new BinaryFormatter();
          var stream = new MemoryStream();
          formatter.Serialize(stream, validators);
          stream.Close();

          var urlHandle = new UrlHandle();
          urlHandle["validators"] = Convert.ObjectToBase64(stream.ToArray());
          urlHandle["WarningTitle"] = text;
          urlHandle["WarningHelp"] = Texts.YOU_CAN_REVIEW_THE_LIST_OF_ERROR_AT_ANY_TIME_BY_USING_THE_VALIDATE_BUTTON_IN_CONTENT_EDITOR;
          var url = new UrlString("/sitecore/shell/-/xaml/Sitecore.Shell.Applications.ContentEditor.Dialogs.ValidationResult.aspx");
          urlHandle.Add(url);

          SheerResponse.ShowModalDialog(new ModalDialogOptions(url.ToString()) { Width = "850" });
          if (!string.IsNullOrEmpty(text))
          {
            args.AddMessage(text);
          }
        }
        else
        {
          // Add the message if on a background thread
          if (!string.IsNullOrEmpty(text))
          {
            args.AddMessage(text);
          }
        }

      }

      args.AbortPipeline();
    }

    #endregion

    #region Private methods

    /// <summary>
    /// Gets the max result.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <param name="commandItem">The command item.</param>
    /// <returns></returns>
    static ValidatorResult GetMaxResult([NotNull] WorkflowPipelineArgs args, [NotNull] Item commandItem)
    {
      Assert.ArgumentNotNull(args, "args");
      Assert.ArgumentNotNull(commandItem, "commandItem");

      string maxResultString = GetText(commandItem, "Max Result", args);

      if (!string.IsNullOrEmpty(maxResultString))
      {
        return (ValidatorResult)Enum.Parse(typeof(ValidatorResult), maxResultString, true);
      }

      return ValidatorResult.Warning;
    }

    /// <summary>
    /// Gets the text.
    /// </summary>
    /// <param name="commandItem">The command item.</param>
    /// <param name="field">The field.</param>
    /// <param name="args">The arguments.</param>
    /// <returns></returns>
    [NotNull]
    static string GetText([NotNull] Item commandItem, [NotNull] string field, [NotNull] WorkflowPipelineArgs args)
    {
      Assert.ArgumentNotNull(commandItem, "commandItem");
      Assert.ArgumentNotNull(field, "field");
      Assert.ArgumentNotNull(args, "args");

      string text = commandItem[field];

      if (text.Length > 0)
      {
        return ReplaceVariables(text, args);
      }

      return String.Empty;
    }

    /// <summary>
    /// Replaces the variables.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The replaced text.</returns>
    [NotNull]
    static string ReplaceVariables([NotNull] string text, [NotNull] WorkflowPipelineArgs args)
    {
      Assert.ArgumentNotNull(text, "text");
      Assert.ArgumentNotNull(args, "args");

      text = text.Replace("$itemPath$", args.DataItem.Paths.FullPath);
      text = text.Replace("$itemLanguage$", args.DataItem.Language.ToString());
      text = text.Replace("$itemVersion$", args.DataItem.Version.ToString());

      return text;
    }

    /// <summary>
    /// Validates the specified validators.
    /// </summary>
    /// <param name="validators">The validators.</param>
    /// <param name="args">The args.</param>
    /// <returns>The validate result.</returns>
    static ValidatorResult Validate([NotNull] ValidatorCollection validators, [NotNull] WorkflowPipelineArgs args)
    {
      Assert.ArgumentNotNull(validators, "validators");
      Assert.ArgumentNotNull(args, "args");

      ProcessorItem processorItem = args.ProcessorItem;
      if (processorItem == null)
      {
        return ValidatorResult.Valid;
      }

      int timeout = MainUtil.GetInt(GetText(processorItem.InnerItem, "Timeout", args), 10000);
      int sleep = MainUtil.GetInt(GetText(processorItem.InnerItem, "Sleep", args), 500);

      int count = 0;

      while (true)
      {
        ValidatorResult result = ValidatorResult.Valid;

        foreach (BaseValidator validator in validators)
        {
          if (validator.IsEvaluating)
          {
            result = ValidatorResult.Unknown;
            break;
          }

          if (validator.Result > result)
          {
            result = validator.Result;
          }
        }

        if (result != ValidatorResult.Unknown)
        {
          return result;
        }

        count++;

        if (count * sleep > timeout)
        {
          string text = GetText(processorItem.InnerItem, "TimeoutText", args);

          SheerResponse.Alert(text);

          args.AbortPipeline();

          return ValidatorResult.Unknown;
        }

        Thread.Sleep(sleep);

        ValidatorManager.UpdateValidators(validators);
      }
    }

    #endregion
  }
}