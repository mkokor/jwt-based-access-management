﻿using AutoMapper;
using JwtAuth.BLL.DTOs.Requests;
using JwtAuth.BLL.DTOs.Responses;
using JwtAuth.BLL.Utilities.TokenGenerationService;
using JwtAuth.DAL.Entities;
using JwtAuth.DAL.Repositories.UnitOfWork;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JwtAuth.BLL.Services.AuthenticationService
{
    public class AuthenticationService : IAuthenticationService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ITokenGenerationService _tokenGenerationService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMapper _mapper;

        public AuthenticationService(IUnitOfWork unitOfWork, IConfiguration configuration, IMapper mapper, ITokenGenerationService tokenGenerationService, IHttpContextAccessor httpContextAccessor)
        {
            _unitOfWork = unitOfWork;
            _mapper = mapper;
            _tokenGenerationService = tokenGenerationService;
            _httpContextAccessor = httpContextAccessor;
        }

        #region UserRegistration
        private void EncodePlaintextPassword(string plaintextPassword, out byte[] passwordHash, out byte[] passwordSalt)
        {
            using (var hmac = new HMACSHA512())
            {
                passwordSalt = hmac.Key;
                passwordHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintextPassword));
            }
        }

        private void ValidatePasswordStrength(string password)
        {
            // minimum 8 characters
            // minimum one digit
            // minimum one special character
            if (Regex.IsMatch(password, "^(?=.*[0-9])(?=.*[!@#$%^&*])[a-zA-Z0-9!@#$%^&*]{8,}$"))
                return;
            throw new Exception("Password needs to contain minimum of 8 characters, one digit and one special character!");
        }

        private async Task CheckUsernameAvailability(string username)
        {
            var allUsers = await _unitOfWork.UserRepository.GetAllUsers();
            if (allUsers.FirstOrDefault(user => user.Username.Equals(username)) != null)
                throw new Exception("Provided username is not available!");
        }

        public async Task RegisterUser(UserRegistrationRequestDto userRegistrationRequestDto)
        {
            await CheckUsernameAvailability(userRegistrationRequestDto.Username);
            ValidatePasswordStrength(userRegistrationRequestDto.Password);
            EncodePlaintextPassword(userRegistrationRequestDto.Password, out byte[] passwordHash, out byte[] passwordSalt);
            var user = _mapper.Map<User>(userRegistrationRequestDto);
            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.Role = "Basic User";
            await _unitOfWork.UserRepository.CreateUser(user);
            await _unitOfWork.SaveAsync();
        }
        #endregion


        #region UserLogin
        private async Task<User> GetUserByUsername(string username)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsername(username);
            if (user != null)
                return user;
            throw new NullReferenceException("User with provided username does not exist!");
        }

        // Function below checks if provided plaintext password matches with provided hashed password (with specific salt)!
        private void ValidatePasswordHash(string plaintextPassword, byte[] passwordHash, byte[] passwordSalt)
        {
            using (var hmac = new HMACSHA512(passwordSalt))
            {
                var computedPasswordHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintextPassword));
                if (computedPasswordHash.SequenceEqual(passwordHash))
                    return;
                throw new Exception("Password does not match the username!");
            }
        }

        private async Task<RefreshToken> GetRefreshTokenByOwnerId(int ownerId)
        {
            var refreshToken = await _unitOfWork.RefreshTokenRepository.GetRefreshTokenByOwnerId(ownerId);
            if (refreshToken == null)
                throw new NullReferenceException("User with provided identifier does not own refresh token!");
            return refreshToken;
        }

        private async Task<RefreshToken> UpdateRefreshToken(int ownerId)
        {
            var refreshToken = await GetRefreshTokenByOwnerId(ownerId);
            var newRefreshToken = _tokenGenerationService.GenerateRefreshToken(ownerId);
            refreshToken.Value = newRefreshToken.Value;
            refreshToken.CreatedAt = newRefreshToken.CreatedAt;
            refreshToken.ExpiresAt = newRefreshToken.ExpiresAt;
            await _unitOfWork.SaveAsync();
            return refreshToken;
        }

        private async Task<RefreshToken> CreateRefreshToken(int ownerId)
        {
            var refreshToken = await _unitOfWork.RefreshTokenRepository.CreateRefreshToken(_tokenGenerationService.GenerateRefreshToken(ownerId));
            await _unitOfWork.SaveAsync();
            return refreshToken;
        }

        private void SetRefreshTokenInHttpOnlyCookie(RefreshToken refreshToken)
        {
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Expires = refreshToken.ExpiresAt
            };
            _httpContextAccessor.HttpContext.Response.Cookies.Append("refreshToken", refreshToken.Value, cookieOptions); // Adding refresh token in HttpOnly cookie...
        }

        private async Task<RefreshToken?> GetRefreshTokenByValue(string value)
        {
            var refreshToken = await _unitOfWork.RefreshTokenRepository.GetRefreshTokenByValue(value);
            if (refreshToken == null)
                throw new AuthenticationException("Invalid refresh token!");
            return refreshToken;
        }

        private string GetRefreshTokenFromCookie()
        {
            var refreshTokenValue = _httpContextAccessor.HttpContext.Request.Cookies["refreshToken"];
            if (refreshTokenValue == null)
                throw new AuthenticationException("Refresh token could not be found in cookie!");
            return refreshTokenValue;
        }

        private async Task<RefreshToken> ValidateRefreshToken(string refreshTokenValue)
        {
            var refreshToken = await GetRefreshTokenByValue(refreshTokenValue);
            if (refreshToken.ExpiresAt < DateTime.Now)
                throw new AuthenticationException("Refresh token expired!");
            return refreshToken;
        }

        public async Task<UserLoginResponseDto> LogInUser(UserLoginRequestDto userLoginRequestDto)
        {
            var user = await GetUserByUsername(userLoginRequestDto.Username);
            ValidatePasswordHash(userLoginRequestDto.Password, user.PasswordHash, user.PasswordSalt);
            SetRefreshTokenInHttpOnlyCookie(await CreateRefreshToken(user.UserId));
            return new UserLoginResponseDto
            {
                JsonWebToken = _tokenGenerationService.GenerateJwt(user)
            };
        }

        public async Task<JwtRefreshResponseDto> RefreshJwt()
        {
            var refreshToken = await ValidateRefreshToken(GetRefreshTokenFromCookie());
            // Delete refresh token!
            SetRefreshTokenInHttpOnlyCookie(await CreateRefreshToken(refreshToken.OwnerId));
            return new JwtRefreshResponseDto
            {
                JsonWebToken = _tokenGenerationService.GenerateJwt(refreshToken.Owner)
            };
        }
        #endregion


        #region JsonWebTokenOwner
        private string GetJwtOwnerNameIdentifier()
        {
            var jwtOwnerNameIdentifier = _httpContextAccessor.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier);
            if (jwtOwnerNameIdentifier == null)
                throw new AuthenticationException("User is not authenticated!");
            return jwtOwnerNameIdentifier.Value;
        }

        private int GetJwtOwnerId()
        {
            if (_httpContextAccessor.HttpContext == null)
                throw new Exception("Something went wrong!");
            return int.Parse(GetJwtOwnerNameIdentifier());
        }

        public async Task<UserResponseDto> GetJwtOwner()
        {
            var user = await _unitOfWork.UserRepository.GetUserById(GetJwtOwnerId());
            return _mapper.Map<UserResponseDto>(user);
        }
        #endregion
    }
}