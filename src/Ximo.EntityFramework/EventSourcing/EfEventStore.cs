﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ximo.Domain;
using Ximo.EventSourcing;

namespace Ximo.EntityFramework.EventSourcing
{
    /// <summary>
    ///     Implements and event store using Entity Framework.
    /// </summary>
    /// <typeparam name="TAggregateRoot">The type of the aggregate root.</typeparam>
    /// <typeparam name="TEventSet">
    ///     The type of the event set (the actual data model object representing the table of events in
    ///     the database.
    /// </typeparam>
    /// <typeparam name="TContext">The type of the context.</typeparam>
    /// <seealso cref="EfRepository{TContext}" />
    /// <seealso cref="IEventStore{TAggregateRoot}" />
    [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
    public class EfEventStore<TAggregateRoot, TEventSet, TContext> : EfRepository<TContext>, IEventStore<TAggregateRoot>
        where TAggregateRoot : EventSourcedAggregateRoot
        where TEventSet : EfDomainEvent
        where TContext : DbContext
    {
        private readonly JsonObjectSerializer _serializer = new JsonObjectSerializer();

        /// <summary>
        ///     Initializes a new instance of the <see cref="EfEventStore{TAggregateRoot, TEventSet, TContext}" /> class.
        /// </summary>
        /// <param name="context">The data context.</param>
        public EfEventStore(TContext context) : base(context)
        {
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="EfEventStore{TAggregateRoot, TEventSet, TContext}" /> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="domainEventBus">The domain event bus.</param>
        protected EfEventStore(TContext context, IDomainEventBus domainEventBus) : base(context)
        {
            DomainEventBus = domainEventBus;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="EfEventStore{TAggregateRoot, TEventSet, TContext}" /> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="snapshotRepository">The snapshot repository.</param>
        protected EfEventStore(TContext context, ISnapshotRepository<TAggregateRoot> snapshotRepository) : base(context)
        {
            SnapshotRepository = snapshotRepository;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="EfEventStore{TAggregateRoot, TEventSet, TContext}" /> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="domainEventBus">The domain event bus.</param>
        /// <param name="snapshotRepository">The snapshot repository.</param>
        protected EfEventStore(TContext context, IDomainEventBus domainEventBus,
            ISnapshotRepository<TAggregateRoot> snapshotRepository) : this(context)
        {
            DomainEventBus = domainEventBus;
            SnapshotRepository = snapshotRepository;
        }

        /// <summary>
        ///     Gets the domain event bus.
        /// </summary>
        /// <value>The domain event bus.</value>
        protected IDomainEventBus DomainEventBus { get; }

        /// <summary>
        ///     Gets the snapshot repository.
        /// </summary>
        /// <value>The snapshot repository.</value>
        protected ISnapshotRepository<TAggregateRoot> SnapshotRepository { get; }

        private DbSet<TEventSet> PersistedEvents => Context.Set<TEventSet>();
        private bool CanPublishEvents => DomainEventBus != null;
        private bool CanProcessSnapshots => SnapshotRepository != null;

        public virtual void Save(TAggregateRoot aggregateRoot)
        {
            var set = Context.Set<TEventSet>();

            //Check for concurrency issues
            ConcurrencyCheck<TAggregateRoot, TEventSet>.Process(aggregateRoot, set);

            //Persist Events
            foreach (var uncommittedEvent in aggregateRoot.UncommittedEvents)
            {
                var @event = CreateDomainEventRecord(uncommittedEvent);
                PersistedEvents.Add(@event);
            }

            //Check if snapshot is required
            var snapshotRequired = CanProcessSnapshots &&
                                   aggregateRoot.UncommittedEvents.Any(
                                       e => e.Sequence % SnapshotRepository.SnapshotInterval == 0);
            if (snapshotRequired)
            {
                SnapshotRepository.SaveSnapshot(aggregateRoot);
            }

            Context.SaveChanges();

            //Publish events if possible
            if (CanPublishEvents)
            {
                foreach (var uncommittedEvent in aggregateRoot.UncommittedEvents)
                {
                    DomainEventBus.Publish(uncommittedEvent.Event);
                }
            }

            //Mark aggregate root as committed
            aggregateRoot.MarkAsCommitted();
        }

        public TAggregateRoot GetById(Guid id)
        {
            TAggregateRoot aggregateRoot = null;
            IEnumerable<DomainEventEnvelope> eventWrappers;

            if (CanProcessSnapshots)
            {
                var result = SnapshotRepository.GetLatestSnapshot(id);
                if (result != null)
                {
                    aggregateRoot = result.Item1;
                    var lastEventSequence = result.Item2;
                    eventWrappers = GetAggregateEvents(id, lastEventSequence);
                }
                else
                {
                    eventWrappers = GetAggregateEvents(id);
                    if (eventWrappers == null || !eventWrappers.Any())
                    {
                        return null;
                    }
                }
            }
            else
            {
                eventWrappers = GetAggregateEvents(id);
                if (eventWrappers == null || !eventWrappers.Any())
                {
                    return null;
                }
            }

            if (aggregateRoot == null)
            {
                aggregateRoot = CreateDefaultInstanceOfAggregateRoot();
            }

            aggregateRoot.Replay(eventWrappers);

            return aggregateRoot;
        }

        public IEnumerable<DomainEventEnvelope> GetAggregateEvents(Guid aggregateRootId)
        {
            var events =
                PersistedEvents.AsNoTracking()
                    .Where(x => x.AggregateId == aggregateRootId)
                    .OrderBy(x => x.Sequence)
                    .ToList();
            var eventWrappers = events.Select(GetDomainEventWrapperFromEvent);
            return eventWrappers;
        }

        public IEnumerable<DomainEventEnvelope> GetAggregateEvents(Guid aggregateRootId, int startSequenceNumber)
        {
            var events =
                PersistedEvents.AsNoTracking()
                    .Where(x => x.AggregateId == aggregateRootId && x.Sequence >= startSequenceNumber)
                    .ToList();
            var eventWrappers = events.Select(GetDomainEventWrapperFromEvent);
            return eventWrappers;
        }

        public IEnumerable<DomainEventEnvelope> GetAggregateEvents<TDomainEvent>(Guid aggregateRootId)
            where TDomainEvent : class
        {
            var domainEventName = typeof(TDomainEvent).FullName;
            var events =
                PersistedEvents.AsNoTracking()
                    .Where(x => x.AggregateId == aggregateRootId && x.Name.Equals(domainEventName))
                    .ToList();
            var eventWrappers = events.Select(GetDomainEventWrapperFromEvent);
            return eventWrappers;
        }

        public IEnumerable<DomainEventEnvelope> GetAggregateEvents(Guid aggregateRootId,
            IEnumerable<Type> domainEventTypes)
        {
            var domainEventTypeNames = new HashSet<string>(domainEventTypes.Select(t => t.FullName));
            var events =
                PersistedEvents.AsNoTracking()
                    .Where(x => x.AggregateId == aggregateRootId && domainEventTypeNames.Contains(x.Name))
                    .ToList();
            var eventWrappers = events.Select(GetDomainEventWrapperFromEvent);
            return eventWrappers;
        }

        protected virtual TEventSet CreateDomainEventRecord(DomainEventEnvelope uncommittedEvent)
        {
            return (TEventSet) Activator.CreateInstance(typeof(TEventSet), uncommittedEvent);
        }

        private DomainEventEnvelope GetDomainEventWrapperFromEvent(TEventSet @event)
        {
            var domainEvent = _serializer.Deserialize<object>(@event.Payload);
            return new DomainEventEnvelope(@event.EventId, @event.AggregateId, @event.Sequence, @event.AggregateVersion,
                domainEvent);
        }

        protected virtual TAggregateRoot CreateDefaultInstanceOfAggregateRoot()
        {
            var aggregateRootType = typeof(TAggregateRoot).GetTypeInfo();
            var constructor = aggregateRootType
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(c => c.GetParameters().Length == 0);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Cannot create a default instance of '{aggregateRootType.Name}' because it has no parameterless constructor. Either create a parameterless constructor (private, public or protected) or override the 'CreateDefaultInstanceOfAggregateRoot' to create the default EventSourcedAggregateRoot manually.");
            }

            return (TAggregateRoot) constructor.Invoke(new object[0]);
        }
    }
}