using AutoMapper;
using StudentService.Domain.DTOs;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Profiles
{
	public class JobMappingProfile : Profile
	{
		public JobMappingProfile()
		{
			CreateMap<Job, JobBasicInfo>()
				.ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.JobId.ToString()));

			CreateMap<Job, JobDetailInfo>();
		}
	}
}
