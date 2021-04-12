﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Flurl.Http;
using Microsoft.AspNetCore.Http;
using OrderCloud.SDK;

namespace OrderCloud.Catalyst
{	
	public class VerifiedUserContext
	{	
		public IOrderCloudClient OcClient
		{
			get
			{
				if (ocClient == null)
				{
					ocClient = JWT.BuildOrderCloudClient(GetToken());
				}
				return ocClient;
			}
		}
		public string Username => GetToken().Username;
		public bool IsAnonUser => GetToken().AnonOrderID != null;
		public bool IsPortalIssuedToken => GetToken().CompanyInteropID != null;
		public bool IsImpersonationToken => GetToken().ImpersonatingUserDatabaseID != null;
		public ImmutableList<string> AvailableRoles => ImmutableList.ToImmutableList(GetToken().Roles);
		public CommerceRole CommerceRole => GetCommerceRole(GetToken().UserType);
		public string TokenApiUrl => GetToken().ApiUrl;
		public string TokenAuthUrl => GetToken().AuthUrl;
		public string TokenClientID => GetToken().ClientID;
		public DateTime TokenExpiresUTC => GetToken().ExpiresUTC ?? throw new NoUserContextException();
		public DateTime TokenNotValidBeforeUTC => GetToken().NotValidBeforeUTC ?? throw new NoUserContextException();

		private JwtOrderCloud token;
		private IOrderCloudClient ocClient;
		private readonly ISimpleCache _cache;
		private readonly IOrderCloudClient _oc;

		public VerifiedUserContext(ISimpleCache cache, IOrderCloudClient oc)
		{
			_cache = cache;
			_oc = oc;
		}

		public async Task VerifyAsync(HttpRequest request, List<string> requiredRoles = null)
		{
			var token = request.GetOrderCloudToken();
			await VerifyAsync(token, requiredRoles);
		}

		public async Task VerifyAsync(string token, List<string> requiredRoles = null)
		{
			if (string.IsNullOrEmpty(token))
				throw new UnAuthorizedException();

			var parsedToken = new JwtOrderCloud(token);

			if (parsedToken.ClientID == null || parsedToken.NotValidBeforeUTC > DateTime.UtcNow || parsedToken.ExpiresUTC < DateTime.UtcNow)
				throw new UnAuthorizedException();

			// we've validated the token as much as we can on this end, go make sure it's ok on OC	
			var allowValidateTokenRetry = false;
			var isValid = await _cache.GetOrAddAsync(token, TimeSpan.FromDays(1), async () =>
			{
				try
				{
					// some valid tokens - e.g. those from the portal - do not have a "kid"
					if (parsedToken.KeyID == null)
					{
						var user = await _oc.Me.GetAsync(token);
						return user != null && user.Active;
					}
					else
					{
						var publicKey = await _oc.Certs.GetPublicKeyAsync(parsedToken.KeyID);
						return JWT.IsTokenCryptoValid(token, publicKey);
					}
				}
				catch (OrderCloudException ex)
				{
					throw ex;
				}
				catch (FlurlHttpException ex) when (ex.Call.Response?.StatusCode < 500)
				{
					return false;
				}
				catch (Exception ex)
				{
					allowValidateTokenRetry = true;
					return false;
				}
			});
			if (allowValidateTokenRetry)
				await _cache.RemoveAsync(token); // not their fault, don't make them wait 5 min      

			if (!isValid)
				throw new UnAuthorizedException();

			if (requiredRoles != null && requiredRoles.Count > 0 && !requiredRoles.Any(role => parsedToken.Roles.Contains(role)))
			{
				throw new InsufficientRolesException(new InsufficientRolesError()
				{
					SufficientRoles = requiredRoles,
					AssignedRoles = parsedToken.Roles.ToList()
				});
			}
			this.token = parsedToken;
		}
		private JwtOrderCloud GetToken() => token ?? throw new NoUserContextException();

		private static CommerceRole GetCommerceRole(string userType)
		{
			switch (userType?.ToLower())
			{
				case "buyer":
					return CommerceRole.Buyer;
				case "seller":
				case "admin":
					return CommerceRole.Seller;
				case "supplier":
					return CommerceRole.Supplier;
				default:
					throw new Exception("unknown user type: " + userType);
			}
		}
	}
}
