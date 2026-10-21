// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Garnet.server.Auth
{
    public class GarnetNoAuthAuthenticator : IGarnetAuthenticator
    {
        public bool IsAuthenticated => true;

        public bool CanAuthenticate => false;

        public bool HasACLSupport => false;

        public bool Authenticate(ReadOnlySpan<byte> password, ReadOnlySpan<byte> username)
        {
            global::System.Environment.FailFast("Debug.Fail at libs/server/Auth/GarnetNoAuthAuthenticator.cs:19" + " | " + ("No auth authenticator should never authenticate."));
            return false;
        }
    }
}