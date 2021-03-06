﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WikiClientLibrary.Wikibase
{
    public static class EntityExtensions
    {
        public static Task RefreshAsync(this IEnumerable<Entity> entities)
        {
            return RefreshAsync(entities, EntityQueryOptions.None, null, CancellationToken.None);
        }

        public static Task RefreshAsync(this IEnumerable<Entity> entities, EntityQueryOptions options)
        {
            return RefreshAsync(entities, options, null, CancellationToken.None);
        }

        public static Task RefreshAsync(this IEnumerable<Entity> entities, EntityQueryOptions options, ICollection<string> languages)
        {
            return RefreshAsync(entities, options, languages, CancellationToken.None);
        }

        public static Task RefreshAsync(this IEnumerable<Entity> entities, EntityQueryOptions options,
            ICollection<string> languages, CancellationToken cancellationToken)
        {
            return WikibaseRequestHelper.RefreshEntitiesAsync(entities, options, languages, cancellationToken);
        }

    }
}
