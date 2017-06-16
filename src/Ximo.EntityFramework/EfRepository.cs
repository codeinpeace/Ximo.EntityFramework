using Microsoft.EntityFrameworkCore;

namespace Ximo.EntityFramework
{
    public abstract class EfRepository<TContext> : DisposableObject
        where TContext : DbContext
    {
        protected EfRepository(TContext context)
        {
            Context = context;
        }

        protected TContext Context { get; }

        protected override void Disposing()
        {
            Context?.Dispose();
        }
    }
}