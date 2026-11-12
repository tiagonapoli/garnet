// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using Garnet.server.Auth.Settings;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test.cluster
{
    /// <summary>
    /// Replication after the cluster AAD token that established the AOF stream expires, even though the token was rotated in time.
    /// </summary>
    /// <remarks>
    /// Managed Garnet rotates the cluster token with <c>CONFIG SET cluster-password</c> before it expires. <c>UpdateClusterAuth</c> only
    /// affects connections opened afterwards; the replica-side session behind an existing AOF stream keeps the original token, and
    /// <c>GarnetAadAuthenticator.IsAuthorized</c> rejects every command on it once that token's <c>exp</c> passes.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class ClusterReplicationClusterTokenRotationTests : TestBase
    {
        const string Issuer = "https://sts.windows.net/975f013f-7f24-47e8-a7d3-abc4752bf346/";
        const int PrimaryIndex = 0;
        const int ReplicaIndex = 1;
        const int ShortTokenSeconds = 20;
        const int CatchUpSeconds = 30;

        ClusterTestContext context;

        [TearDown]
        public void TearDown() => context?.TearDown();

        [Test]
        [Category("REPLICATION"), Category("CLUSTER-AUTH")]
        public void ReplicationSurvivesExpiryOfRotatedClusterToken()
        {
            context = new ClusterTestContext();
            context.Setup([], testTimeoutSeconds: 180);

            var audience = Guid.NewGuid().ToString();
            var tokens = new JwtTokenGenerator(Issuer, audience);
            var appId = Guid.NewGuid().ToString();
            var objectId = Guid.NewGuid().ToString();
            List<Claim> claims = [new("appidacr", "1"), new("appid", appId), new("http://schemas.microsoft.com/identity/claims/objectidentifier", objectId)];
            var authSettings = new AadAuthenticationSettings([appId], [audience], [Issuer], new MockIssuerSigningTokenProvider([tokens.SecurityKey], context.logger), true);

            var shortTokenExpiry = DateTime.UtcNow.AddSeconds(ShortTokenSeconds);
            var clusterCreds = new ServerCredential { user = objectId, password = tokens.CreateToken(claims, shortTokenExpiry) };
            var clientCreds = new ServerCredential { user = objectId, password = tokens.CreateToken(claims, DateTime.UtcNow.AddMinutes(30)) };
            context.GenerateCredentials([new ServerCredential { user = objectId, IsAdmin = true, IsClearText = true }]);
            context.CreateInstances(2, useAcl: true, clusterCreds: clusterCreds, authenticationSettings: authSettings, enableAOF: true);
            context.CreateConnection(clientCreds: clientCreds);
            _ = context.clusterTestUtils.SimpleSetupCluster(1, 1, logger: context.logger);

            var db = context.clusterTestUtils.GetDatabase();
            db.StringSet("{rot}before", "1");
            context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger);
            ClassicAssert.Less(DateTime.UtcNow, shortTokenExpiry, "cluster setup outlived the short-lived cluster token; raise ShortTokenSeconds");

            // Rotate before expiry, as Managed Garnet's Host.Resp does.
            var rotated = tokens.CreateToken(claims, DateTime.UtcNow.AddMinutes(30));
            foreach (var node in new[] { PrimaryIndex, ReplicaIndex })
                ClassicAssert.AreEqual("OK", (string)context.clusterTestUtils.GetServer(node).Execute("CONFIG", "SET", "cluster-username", objectId, "cluster-password", rotated));

            while (DateTime.UtcNow < shortTokenExpiry.AddSeconds(2))
            {
                context.cts.Token.ThrowIfCancellationRequested();
                Thread.Sleep(250);
            }

            for (var i = 0; i < 100; i++)
                db.StringSet($"{{rot}}after{i}", i);

            using var catchUp = CancellationTokenSource.CreateLinkedTokenSource(context.cts.Token);
            catchUp.CancelAfter(TimeSpan.FromSeconds(CatchUpSeconds));
            try
            {
                context.clusterTestUtils.WaitForReplicaAofSync(PrimaryIndex, ReplicaIndex, context.logger, catchUp.Token);
            }
            catch (OperationCanceledException) when (!context.cts.IsCancellationRequested)
            {
                var primary = context.clusterTestUtils.GetReplicationOffset(PrimaryIndex, context.logger);
                var replica = context.clusterTestUtils.GetReplicationOffset(ReplicaIndex, context.logger);
                Assert.Fail($"replica stopped receiving AOF {CatchUpSeconds}s after the original cluster token expired, although cluster-password was rotated to a valid token beforehand (primary offset {primary}, replica offset {replica})");
            }
        }
    }
}
