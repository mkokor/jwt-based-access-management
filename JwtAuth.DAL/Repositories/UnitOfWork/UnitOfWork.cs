﻿using JwtAuth.DAL.Repositories.RefreshTokenRepository;
using JwtAuth.DAL.Repositories.UserRepository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JwtAuth.DAL.Repositories.UnitOfWork
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _appDbContext;

        public IUserRepository UserRepository { get; private set; }
        public IRefreshTokenRepository RefreshTokenRepository { get; private set; }

        public UnitOfWork(AppDbContext appDbContext)
        {
            _appDbContext = appDbContext;
            UserRepository = new UserRepository.UserRepository(_appDbContext);
            RefreshTokenRepository = new RefreshTokenRepository.RefreshTokenRepository(_appDbContext);
        }

        public async Task SaveAsync()
        {
            await _appDbContext.SaveChangesAsync();
        }
    }
}