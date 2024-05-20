using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Newtonsoft.Json;
using System.Threading;
using System.Text;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using Arbitrer.Messages;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Arbitrer.RabbitMQ
{
  /// <summary>
  /// Class for dispatching messages to RabbitMQ and handling responses.
  /// </summary>
  public class MessageDispatcher : IExternalMessageDispatcher, IDisposable
  {
    /// <summary>
    /// Represents the options for the message dispatcher.
    /// </summary>
    private readonly MessageDispatcherOptions options;

    /// <summary>
    /// The logger for the MessageDispatcher class.
    /// </summary>
    /// <typeparam name="MessageDispatcher">The type of the class that the logger is associated with.</typeparam>
    private readonly ILogger<MessageDispatcher> logger;

    private readonly ArbitrerOptions arbitrerOptions;

    /// <summary>
    /// Stores an instance of an object that implements the IConnection interface.
    /// </summary>
    private IConnection _connection = null;

    /// <summary>
    /// The channel used for sending messages.
    /// </summary>
    private IModel _sendChannel = null;

    /// <summary>
    /// Represents the name of the reply queue.
    /// </summary>
    private string _replyQueueName = null;

    /// <summary>
    /// Represents an asynchronous event-based consumer for sending messages.
    /// </summary>
    private AsyncEventingBasicConsumer _sendConsumer = null;

    /// <summary>
    /// The unique identifier of the consumer.
    /// </summary>
    private string _consumerId = null;

    /// <summary>
    /// Dictionary that maps callback strings to TaskCompletionSource objects.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _callbackMapper =
      new ConcurrentDictionary<string, TaskCompletionSource<string>>();


    /// <summary>
    /// Constructor for the MessageDispatcher class. </summary> <param name="options">The options for the MessageDispatcher.</param> <param name="logger">The logger for the MessageDispatcher.</param>
    /// /
    public MessageDispatcher(
      IOptions<MessageDispatcherOptions> options,
      ILogger<MessageDispatcher> logger, IOptions<ArbitrerOptions> arbitrerOptions)
    {
      this.options = options.Value;
      this.logger = logger;
      this.arbitrerOptions = arbitrerOptions.Value;

      this.InitConnection();
    }

    /// Initializes the RabbitMQ connection and sets up the necessary channels and consumers.
    /// /
    private void InitConnection()
    {
      // Ensuring we have a connection object
      if (_connection == null)
      {
        logger.LogInformation($"Creating RabbitMQ Connection to '{options.HostName}'...");
        var factory = new ConnectionFactory
        {
          HostName = options.HostName,
          UserName = options.UserName,
          Password = options.Password,
          VirtualHost = options.VirtualHost,
          Port = options.Port,
          ClientProvidedName = options.ClientName,
          DispatchConsumersAsync = true,
        };

        _connection = factory.CreateConnection();
      }

      _sendChannel = _connection.CreateModel();
      _sendChannel.ExchangeDeclare(Constants.ArbitrerExchangeName, ExchangeType.Topic);
      // _channel.ConfirmSelect();

      var queueName = $"{options.QueueName}.{Process.GetCurrentProcess().Id}.{DateTime.Now.Ticks}";
      _replyQueueName = _sendChannel.QueueDeclare(queue: queueName).QueueName;
      _sendConsumer = new AsyncEventingBasicConsumer(_sendChannel);
      _sendConsumer.Received += (s, ea) =>
      {
        TaskCompletionSource<string> tcs = null;
        try
        {
          if (!_callbackMapper.TryRemove(ea.BasicProperties.CorrelationId, out tcs))
            return Task.CompletedTask;


          var body = ea.Body.ToArray();
          var response = Encoding.UTF8.GetString(body);
          tcs.TrySetResult(response);
        }
        catch (Exception ex)
        {
          logger.LogError($"Error deserializing response: {ex.Message}", ex);
          tcs?.TrySetException(ex);
        }

        return Task.CompletedTask;
      };

      _sendChannel.BasicReturn += (s, ea) =>
      {
        if (!_callbackMapper.TryRemove(ea.BasicProperties.CorrelationId, out var tcs)) return;
        tcs.TrySetException(new Exception($"Unable to deliver required action: {ea.RoutingKey}"));
      };

      _sendChannel.BasicQos(0, Math.Max(options.PerConsumerQos, (ushort)1), false);
      this._consumerId = _sendChannel.BasicConsume(queue: _replyQueueName, autoAck: true, consumer: _sendConsumer);
    }


    /// <summary>
    /// Dispatches a request and waits for the response.
    /// </summary>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    /// <typeparam name="TResponse">The type of the response.</typeparam>
    /// <param name="request">The request object.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task representing the response message.</returns>
    public async Task<Messages.ResponseMessage<TResponse>> Dispatch<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
    {
      var message = JsonConvert.SerializeObject(request, options.SerializerSettings);

      var correlationId = Guid.NewGuid().ToString();

      var tcs = new TaskCompletionSource<string>();
      var rr = _callbackMapper.TryAdd(correlationId, tcs);

      _sendChannel.BasicPublish(
        exchange: Constants.ArbitrerExchangeName,
        routingKey: typeof(TRequest).TypeQueueName(arbitrerOptions),
        mandatory: true,
        body: Encoding.UTF8.GetBytes(message),
        basicProperties: GetBasicProperties(correlationId));

      cancellationToken.Register(() => _callbackMapper.TryRemove(correlationId, out var tmp));
      var result = await tcs.Task;

      return JsonConvert.DeserializeObject<Messages.ResponseMessage<TResponse>>(result, options.SerializerSettings);
    }

    /// <summary>
    /// Sends a notification message to the specified exchange and routing key.
    /// </summary>
    /// <typeparam name="TRequest">The type of the request message.</typeparam>
    /// <param name="request">The request message to send.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the notification operation.</param>
    /// <returns>A task representing the asynchronous notification operation.</returns>
    public Task Notify<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : INotification
    {
      var message = JsonConvert.SerializeObject(request, options.SerializerSettings);

      logger.LogInformation($"Sending message to: {Constants.ArbitrerExchangeName}/{request.GetType().TypeQueueName(arbitrerOptions)}");

      _sendChannel.BasicPublish(
        exchange: Constants.ArbitrerExchangeName,
        routingKey: request.GetType().TypeQueueName(arbitrerOptions),
        mandatory: false,
        body: Encoding.UTF8.GetBytes(message)
      );

      return Task.CompletedTask;
    }


    /// <summary>
    /// Retrieves the basic properties for a given correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID associated with the properties.</param>
    /// <returns>The basic properties object.</returns>
    private IBasicProperties GetBasicProperties(string correlationId)
    {
      var props = _sendChannel.CreateBasicProperties();
      props.CorrelationId = correlationId;
      props.ReplyTo = _replyQueueName;
      return props;
    }

    /// <summary>
    /// Disposes of the resources used by the object.
    /// </summary>
    public void Dispose()
    {
      try
      {
        this.logger.LogInformation("Closing Connection...");
        _sendChannel?.BasicCancel(_consumerId);
        _sendChannel?.Close();
        // _connection.Close();
      }
      catch (Exception)
      {
      }
    }
  }
}