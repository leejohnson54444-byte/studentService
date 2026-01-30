using AutoMapper;
using StudentService.Domain.DTOs;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Profiles
{
	public class CompanyMappingProfile : Profile
	{
		public CompanyMappingProfile()
		{
			CreateMap<Company, CompanyInfo>()
				.ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
				.ForMember(dest => dest.ThumbsUp, opt => opt.MapFrom(src => src.ThumbsReceived != null ? src.ThumbsReceived.Up : (int?)null))
				.ForMember(dest => dest.ThumbsDown, opt => opt.MapFrom(src => src.ThumbsReceived != null ? src.ThumbsReceived.Down : (int?)null));
		}
	}
}
