﻿using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Inceptum.Messaging.Transports;

namespace Inceptum.Messaging.InMemory
{
    public class InMemoryTransportFactory : ITransportFactory
    {
        readonly Dictionary<TransportInfo,InMemoryTransport> m_Transports=new Dictionary<TransportInfo, InMemoryTransport>(); 
        public string Name { get { return "InMemory"; } }
        public ITransport Create(TransportInfo transportInfo, Action onFailure)
        {
            lock (m_Transports)
            {
                InMemoryTransport transport;
                if (!m_Transports.TryGetValue(transportInfo, out transport))
                {
                    transport=new InMemoryTransport();
                    m_Transports.Add(transportInfo,transport);
                }
                return transport;
            }
        }
    }

    internal class InMemoryTransport : ITransport
    {
        readonly Dictionary<string,Subject<BinaryMessage>> m_Topics=new Dictionary<string, Subject<BinaryMessage>>();


        public Subject<BinaryMessage> this[string name]
        {
            get
            {
                lock (m_Topics)
                {
                    Subject<BinaryMessage> topic;
                    if (!m_Topics.TryGetValue(name, out topic))
                    {
                        topic=new Subject<BinaryMessage>();
                        m_Topics[name] = topic;
                    }
                    return topic;
                }
            }
        }

        public IDisposable CreateTemporary(string name)
        {
            lock (m_Topics)
            {
                Subject<BinaryMessage> topic;
                if (m_Topics.TryGetValue(name, out topic))
                {
                    throw new ArgumentException("topic already exists", "name");
                }
                topic = new Subject<BinaryMessage>();
                m_Topics[name] = topic;
                return Disposable.Create(() =>
                    {
                        lock (m_Topics)
                        {
                            m_Topics.Remove(name);
                        }
                    });
            }
        }
        public void Dispose()
        {

        }

        public IProcessingGroup CreateProcessingGroup(Action onFailure)
        {
            return new InMemoryProcessingGroup(this);
        }
    }

     
 
    internal class InMemoryProcessingGroup : IProcessingGroup
    {
        private readonly InMemoryTransport m_Transport;
        readonly EventLoopScheduler m_Scheduler=new EventLoopScheduler(ts => new Thread(ts));
        readonly CompositeDisposable m_Subscriptions=new CompositeDisposable();

        public InMemoryProcessingGroup(InMemoryTransport queues)
        {
            m_Transport = queues;
        }

        public void Send(string destination, BinaryMessage message, int ttl)
        {
            m_Transport[destination].OnNext(message);
        }

        public IDisposable Subscribe(string destination, Action<BinaryMessage> callback, string messageType)
        {
            var subscribe = m_Transport[destination].Where(m => m.Type == messageType || messageType == null).ObserveOn(m_Scheduler).Subscribe(callback);
            m_Subscriptions.Add(subscribe);
            return subscribe;
        }

        public RequestHandle SendRequest(string destination, BinaryMessage message, Action<BinaryMessage> callback)
        {
            var replyTo = Guid.NewGuid().ToString();
            var responseTopic = m_Transport.CreateTemporary(replyTo);

            var request = new RequestHandle(callback, responseTopic.Dispose, cb => Subscribe(replyTo, cb, null));
            message.Headers["ReplyTo"] = replyTo;
            Send(destination,message,0);
            return request;
        }

        public IDisposable RegisterHandler(string destination, Func<BinaryMessage, BinaryMessage> handler, string messageType)
        {
            var subscription = Subscribe(destination, request =>
                {
                    string replyTo;
                    request.Headers.TryGetValue("ReplyTo",out replyTo);
                    if(replyTo==null)
                        return;

                    var response = handler(request);
                    string correlationId;
                    if(request.Headers.TryGetValue("ReplyTo", out correlationId))
                        response.Headers["CorrelationId"] = correlationId;
                    Send(replyTo, response,0);
            }, messageType);
            return subscription;
        }



        public void Dispose()
        {
            m_Subscriptions.Dispose();
            m_Scheduler.Dispose();
        }
      
    }
}